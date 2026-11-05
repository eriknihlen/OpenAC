//! Tests of the communication owner: inbound routing, outbound sends and
//! the files it keeps.

use ac_bits::Writer;
use ac_core::chat::{ChatKind, LogTextType, client_text};
use ac_proto::chat::{build_chat_channel, build_emote, build_talk, build_talk_direct, build_tell};
use ac_proto::client_commands::{build_query_age, build_tele_to_lifestone};
use ac_proto::turbine_chat::{
    BlobType, DispatchType, Payload, TurbineChat, TurbineChatChannels, build_turbine_chat_to_room,
    chat_type,
};
use ac_proto::{GameEventType, Inbound};

use super::log_file::tests::TempDir;
use super::*;
use crate::session::{Outbox, Queue};

const PLAYER: u32 = 0x5000_0001;
const OTHER: u32 = 0x5000_0042;
const NOW: i64 = 1_787_313_600_000;

fn owner() -> Communication {
    let mut communication = Communication::new(CommunicationPaths::default());
    communication.entered_world(PLAYER, "World", "+Acdream", NOW);
    communication
}

fn facts() -> PlayerFacts {
    PlayerFacts {
        guid: PLAYER,
        public_weenie_bitfield: Some(0x8),
        is_away: false,
        membership: Membership::default(),
        ..PlayerFacts::default()
    }
}

fn submit(communication: &mut Communication, outbox: &mut Outbox, line: &str) -> SubmitOutcome {
    submit_with(communication, outbox, line, Defaults::default(), true)
}

fn submit_with(
    communication: &mut Communication,
    outbox: &mut Outbox,
    line: &str,
    defaults: Defaults<'_>,
    active: bool,
) -> SubmitOutcome {
    let mut options = crate::character::CharacterOptions::new();
    let mut out = Outbound {
        outbox,
        player: facts(),
        now_unix_ms: NOW,
        active,
        options: &mut options,
    };
    communication.submit(line, defaults, &mut out)
}

fn sent(outbox: &mut Outbox) -> Vec<Vec<u8>> {
    outbox.drain().map(|o| o.body).collect()
}

fn lines(communication: &mut Communication) -> Vec<String> {
    communication
        .drain_events()
        .filter_map(|e| match e {
            CommunicationEvent::Line(line) => Some(line.text),
            CommunicationEvent::InterfaceText(_)
            | CommunicationEvent::StatusNotice { .. }
            | CommunicationEvent::Confirmation(_) => None,
        })
        .collect()
}

fn interface_texts(communication: &mut Communication) -> Vec<String> {
    communication
        .drain_events()
        .filter_map(|e| match e {
            CommunicationEvent::InterfaceText(text) => Some(text),
            CommunicationEvent::Line(_)
            | CommunicationEvent::StatusNotice { .. }
            | CommunicationEvent::Confirmation(_) => None,
        })
        .collect()
}

fn string16l(w: &mut Writer, text: &str) {
    w.string16l(text).unwrap();
}

fn game_event(event: GameEventType, payload: &[u8]) -> Vec<u8> {
    let mut w = Writer::new();
    w.u32(0xF7B0);
    w.u32(PLAYER);
    w.u32(1);
    w.u32(event.0);
    w.bytes(payload);
    w.into_vec()
}

fn apply(communication: &mut Communication, body: &[u8]) {
    let message = Inbound::parse(body).expect("well-formed test message");
    communication.apply(&message, NOW);
}

// -- Outbound bytes ----------------------------------------------------------
// Golden values: OpenAC tests/AcDream.Core.Net.Tests/WorldSessionChatTests.cs

#[test]
fn say_tell_and_channel_send_the_built_actions_with_one_counter() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "hello");
    submit(&mut communication, &mut outbox, "/tell Alice, hey");
    submit(&mut communication, &mut outbox, "/f raid plan");
    submit(&mut communication, &mut outbox, "/ab allegiance ping");
    assert_eq!(
        sent(&mut outbox),
        [
            build_talk(1, "hello").unwrap(),
            build_tell(2, "Alice", "hey").unwrap(),
            build_chat_channel(3, 0x0000_0800, "raid plan").unwrap(),
            build_chat_channel(4, 0x0200_0000, "allegiance ping").unwrap(),
        ]
    );
}

#[test]
fn client_commands_send_their_actions() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "/ls");
    submit(&mut communication, &mut outbox, "/age");
    submit(&mut communication, &mut outbox, ":waves.");
    assert_eq!(
        sent(&mut outbox),
        [
            build_tele_to_lifestone(1),
            build_query_age(2, 0),
            build_emote(3, "waves.").unwrap(),
        ]
    );
}

// Golden values: OpenAC tests/AcDream.Core.Net.Tests/WorldSessionAdministrationCommandTests.cs
// (each administration command goes out through its retail builder) and
// tests/AcDream.UI.Abstractions.Tests/Panels/Chat/ChatCommandRouterTests.cs
// (`AdministrationCommand_PublishesTypedClientCommand`).

#[test]
fn administration_commands_send_their_actions() {
    use ac_proto::{allegiance, house};
    let mut communication = owner();
    let mut outbox = Outbox::new();
    for line in [
        "/allegiance boot Lord Bob",
        "/allegiance officer list",
        "/allegiance info +Horan",
        "/allegiance broadcast hear ye",
        "/motd set Welcome home",
        "/house storage add Lord Bob",
        "/house guest list",
        "/house hooks off",
        "/house open",
        "/hslist villa",
    ] {
        submit(&mut communication, &mut outbox, line);
    }
    assert_eq!(
        sent(&mut outbox),
        [
            allegiance::build_break_allegiance_boot(1, "Lord Bob", false).unwrap(),
            allegiance::build_list_officers(2),
            allegiance::build_info_request(3, "+Horan").unwrap(),
            build_chat_channel(4, 0x0200_0000, "hear ye").unwrap(),
            allegiance::build_set_motd(5, "Welcome home").unwrap(),
            house::build_change_storage_permission(6, "Lord Bob", true).unwrap(),
            house::build_request_full_guest_list(7),
            house::build_set_hooks_visibility(8, false),
            house::build_set_open_house_status(9, true),
            house::build_list_available_houses(10, 2),
        ]
    );
    assert_eq!(
        lines(&mut communication),
        ["Attempting to boot Lord Bob..."]
    );
    submit(
        &mut communication,
        &mut outbox,
        "/allegiance officer add 7 Bob",
    );
    assert!(sent(&mut outbox).is_empty());
    assert_eq!(
        interface_texts(&mut communication),
        [
            "Please specify a valid officer level as a number between 1 and 3. Check the game help files for more information on officer levels."
        ]
    );
}

// Golden values: OpenAC tests/AcDream.Runtime.Tests/Chat/LiveChatCommandRouteTests.cs

#[test]
fn routes_keep_their_order_and_echoing_channels_print_nothing_locally() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    for line in [
        "@server",
        "say",
        "/tell Bob secret",
        "/f group",
        "/admin admin",
    ] {
        submit(&mut communication, &mut outbox, line);
    }
    assert_eq!(
        sent(&mut outbox),
        [
            build_talk(1, "@server").unwrap(),
            build_talk(2, "say").unwrap(),
            build_tell(3, "Bob", "secret").unwrap(),
            build_chat_channel(4, 0x800, "group").unwrap(),
            build_chat_channel(5, 0x2, "admin").unwrap(),
        ]
    );
    assert!(communication.chat().is_empty());
}

#[test]
fn nothing_is_sent_outside_the_world() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    submit_with(
        &mut communication,
        &mut outbox,
        "/tell Bob, hello",
        Defaults::default(),
        false,
    );
    assert!(outbox.is_empty());
    assert_eq!(communication.targets().last_outgoing_tell_target(), None);
}

// Golden values: OpenAC tests/AcDream.Runtime.Tests/Chat/TellTargetingTests.cs

#[test]
fn a_typed_tell_records_the_addressee_for_retell_and_prints_nothing() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    assert_eq!(
        submit(&mut communication, &mut outbox, "/tell Bob, hello"),
        SubmitOutcome::Sent
    );
    assert_eq!(
        communication.targets().last_outgoing_tell_target(),
        Some("Bob")
    );
    assert_eq!(
        submit(&mut communication, &mut outbox, "/rt hello again"),
        SubmitOutcome::Sent
    );
    assert_eq!(
        sent(&mut outbox),
        [
            build_tell(1, "Bob", "hello").unwrap(),
            build_tell(2, "Bob", "hello again").unwrap(),
        ]
    );
    assert!(communication.chat().is_empty());
}

#[test]
fn retell_without_an_earlier_tell_refuses_in_the_status_overlay() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    assert_eq!(
        submit(&mut communication, &mut outbox, "/rt hello"),
        SubmitOutcome::ClientHandled
    );
    assert!(outbox.is_empty());
    assert!(communication.chat().is_empty());
    assert_eq!(
        interface_texts(&mut communication),
        ["You must first provide a name using @tell"]
    );
    communication.tick(0.0);
    communication.tick(0.0);
    assert_eq!(communication.spew_box().len(), 1);
}

#[test]
fn a_picked_target_is_spoken_to_by_id_and_leaves_retell_alone() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    let picked = Defaults {
        channel: ChatChannelKind::Tell,
        tell_target: Some("Aun Tanua"),
        tell_target_guid: 0x8000_ABCD,
    };
    submit(&mut communication, &mut outbox, "/tell Bob, hello");
    submit_with(&mut communication, &mut outbox, "and you", picked, true);
    submit(&mut communication, &mut outbox, "/rt again");
    assert_eq!(
        sent(&mut outbox),
        [
            build_tell(1, "Bob", "hello").unwrap(),
            build_talk_direct(2, 0x8000_ABCD, "and you").unwrap(),
            build_tell(3, "Bob", "again").unwrap(),
        ]
    );
    assert_eq!(
        communication.targets().last_outgoing_tell_target(),
        Some("Bob")
    );

    let mut communication = owner();
    submit_with(&mut communication, &mut outbox, "hello", picked, true);
    assert_eq!(
        submit(&mut communication, &mut outbox, "/rt again"),
        SubmitOutcome::ClientHandled
    );
    assert_eq!(sent(&mut outbox).len(), 1);
}

#[test]
fn a_received_tell_makes_reply_reach_the_teller() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    let mut w = Writer::new();
    string16l(&mut w, "psst");
    string16l(&mut w, "Bestie");
    w.u32(OTHER);
    w.u32(PLAYER);
    w.u32(LogTextType::Tell.as_u32());
    apply(
        &mut communication,
        &game_event(GameEventType::TELL, &w.into_vec()),
    );
    assert_eq!(lines(&mut communication), ["Bestie tells you, \"psst\""]);
    submit(&mut communication, &mut outbox, "/r hi there");
    assert_eq!(
        sent(&mut outbox),
        [build_tell(1, "Bestie", "hi there").unwrap()]
    );
}

// -- Inbound ----------------------------------------------------------------
// Golden values: OpenAC tests/AcDream.Core.Net.Tests/GameEventWiringTests.cs

#[test]
fn a_channel_broadcast_reaches_the_transcript() {
    let mut communication = owner();
    let mut w = Writer::new();
    w.u32(42);
    string16l(&mut w, "Alice");
    string16l(&mut w, "hi");
    apply(
        &mut communication,
        &game_event(GameEventType::CHANNEL_BROADCAST, &w.into_vec()),
    );
    let entry = communication.chat().entries().next().unwrap().clone();
    assert_eq!(entry.kind, ChatKind::Channel);
    assert_eq!(entry.sender, "Alice");
    assert_eq!(entry.log_text_type, 0x08);
}

#[test]
fn clear_and_clear_all_empty_the_one_transcript() {
    // OpenAC `RuntimeClientCommandBindings`: `ClearChat` clears the
    // communication owner's chat whatever the argument (every chat window
    // shows the one transcript).
    for line in ["/clear", "/clear all"] {
        let mut communication = owner();
        let mut outbox = Outbox::new();
        let mut w = Writer::new();
        w.u32(42);
        string16l(&mut w, "Alice");
        string16l(&mut w, "hi");
        apply(
            &mut communication,
            &game_event(GameEventType::CHANNEL_BROADCAST, &w.into_vec()),
        );
        assert_eq!(communication.chat().entries().count(), 1);
        submit(&mut communication, &mut outbox, line);
        assert_eq!(communication.chat().entries().count(), 0, "{line}");
        assert!(sent(&mut outbox).is_empty(), "{line}");
    }
}

#[test]
fn a_friends_update_replaces_the_list() {
    let mut communication = owner();
    let mut w = Writer::new();
    w.u32(1);
    w.u32(PLAYER);
    w.u32(1);
    w.u32(0);
    string16l(&mut w, "Alice");
    w.u32(0);
    w.u32(0);
    w.u32(0); // full
    apply(
        &mut communication,
        &game_event(GameEventType::FRIENDS_LIST_UPDATE, &w.into_vec()),
    );
    let friends = communication.friends().entries();
    assert_eq!(friends.len(), 1);
    assert_eq!(friends[0].name, "Alice");
    assert!(friends[0].online);
}

#[test]
fn popups_and_age_answers_read_as_retail() {
    let mut communication = owner();
    let mut w = Writer::new();
    string16l(&mut w, "A modal message");
    apply(
        &mut communication,
        &game_event(GameEventType::POPUP_STRING, &w.into_vec()),
    );
    assert_eq!(
        communication.chat().entries().last().unwrap().kind,
        ChatKind::Popup
    );
    for (name, age, expected) in [
        ("", "2d 4h", "You have played for 2d 4h."),
        ("Alice", "1y 2mo", "Alice has played for 1y 2mo."),
    ] {
        let mut w = Writer::new();
        string16l(&mut w, name);
        string16l(&mut w, age);
        apply(
            &mut communication,
            &game_event(GameEventType::QUERY_AGE_RESPONSE, &w.into_vec()),
        );
        assert_eq!(
            communication.chat().entries().last().unwrap().text,
            expected
        );
    }
}

#[test]
fn weenie_errors_route_by_their_text_class() {
    let mut communication = owner();
    let code = |code: u32| {
        let mut w = Writer::new();
        w.u32(code);
        game_event(GameEventType::WEENIE_ERROR, &w.into_vec())
    };
    // Default class: the transcript.
    apply(&mut communication, &code(0x04A));
    let entry = communication.chat().entries().last().unwrap().clone();
    assert_eq!(
        (entry.kind, entry.text.as_str(), entry.log_text_type),
        (ChatKind::System, "Ack! You killed yourself!", 0)
    );
    // Unmapped and silent codes: nothing.
    for silent in [0x9C, 0x3B, 0x3C] {
        apply(&mut communication, &code(silent));
    }
    assert_eq!(communication.chat().len(), 1);
    // Client-local class: the status overlay, never the transcript.
    communication.drain_events().for_each(drop);
    apply(&mut communication, &code(0x024));
    assert_eq!(communication.chat().len(), 1);
    assert_eq!(
        interface_texts(&mut communication),
        [client_text::CANT_JUMP_IN_AIR]
    );
    // Magic class: the transcript, keeping its class.
    apply(&mut communication, &code(0x402));
    let entry = communication.chat().entries().last().unwrap().clone();
    assert_eq!(entry.text, "Your spell fizzled.");
    assert_eq!(entry.log_text_type, LogTextType::Magic.as_u32());
}

#[test]
fn a_weenie_error_with_a_string_fills_it_in() {
    let mut communication = owner();
    let mut w = Writer::new();
    w.u32(0x0521);
    string16l(&mut w, "Caith");
    apply(
        &mut communication,
        &game_event(GameEventType::WEENIE_ERROR_WITH_STRING, &w.into_vec()),
    );
    assert_eq!(
        lines(&mut communication),
        ["Caith has been added to the list of people you can hear."]
    );
}

#[test]
fn a_transient_string_is_client_local() {
    let mut communication = owner();
    let mut w = Writer::new();
    string16l(&mut w, "You are too encumbered to carry that!");
    apply(
        &mut communication,
        &game_event(GameEventType::COMMUNICATION_TRANSIENT_STRING, &w.into_vec()),
    );
    assert!(communication.chat().is_empty());
    assert_eq!(
        interface_texts(&mut communication),
        ["You are too encumbered to carry that!"]
    );
}

#[test]
fn the_players_own_speech_reads_you() {
    let mut communication = owner();
    for (name, guid, expected) in [
        ("+Acdream", PLAYER, "You say, \"hello\""),
        ("Bob", OTHER, "Bob says, \"hello\""),
    ] {
        let mut w = Writer::new();
        w.u32(0x02BB);
        string16l(&mut w, "hello");
        string16l(&mut w, name);
        w.u32(guid);
        w.u32(2);
        apply(&mut communication, &w.into_vec());
        assert_eq!(lines(&mut communication), [expected]);
    }
}

#[test]
fn a_bad_payload_is_counted_not_applied() {
    let mut communication = owner();
    apply(
        &mut communication,
        &game_event(GameEventType::TELL, &[1, 2]),
    );
    assert!(communication.chat().is_empty());
    assert_eq!(communication.parse_failures().get(&0xF7B0_02BD), Some(&1));
}

// -- Chat rooms -----------------------------------------------------------------

fn rooms_received(communication: &mut Communication) {
    let channels = TurbineChatChannels {
        allegiance: 0x10,
        general: 0x11,
        trade: 0x12,
        lfg: 0x13,
        roleplay: 0x14,
        olthoi: 0x15,
        society: 0x16,
        ..TurbineChatChannels::default()
    };
    apply(
        communication,
        &game_event(
            GameEventType::SET_TURBINE_CHAT_CHANNELS,
            &channels.to_payload(),
        ),
    );
}

#[test]
fn room_lines_go_out_as_turbine_chat_with_a_cookie_and_no_action_number() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    rooms_received(&mut communication);
    submit(&mut communication, &mut outbox, "/general hello all");
    submit(&mut communication, &mut outbox, "/a for the monarch");
    submit(&mut communication, &mut outbox, "hi");
    let bodies: Vec<_> = outbox.drain().collect();
    assert_eq!(
        bodies[0].body,
        build_turbine_chat_to_room(0x11, chat_type::GENERAL, PLAYER, "hello all", 1).unwrap()
    );
    assert_eq!(bodies[0].queue, Queue::Ui);
    assert_eq!(
        bodies[1].body,
        build_turbine_chat_to_room(0x10, chat_type::ALLEGIANCE, PLAYER, "for the monarch", 2)
            .unwrap()
    );
    // The room lines took no action number.
    assert_eq!(bodies[2].body, build_talk(1, "hi").unwrap());
}

#[test]
fn allegiance_without_rooms_uses_the_broadcast_channel() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "/a hello");
    assert_eq!(
        sent(&mut outbox),
        [build_chat_channel(1, 0x0200_0000, "hello").unwrap()]
    );
}

#[test]
fn a_room_the_player_does_not_hear_is_refused_locally() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "/trade wts");
    assert!(outbox.is_empty());
    assert_eq!(
        lines(&mut communication),
        [client_text::TURBINE_CHAT_UNAVAILABLE]
    );
    rooms_received(&mut communication);
    submit(&mut communication, &mut outbox, "/roleplay *bows*");
    assert!(outbox.is_empty());
    assert_eq!(
        lines(&mut communication),
        [ac_core::chat::weenie_errors::format(0x0551, Some("Roleplay")).unwrap()]
    );
}

#[test]
fn a_room_line_heard_is_shown_under_the_room_name() {
    let mut communication = owner();
    let message = TurbineChat {
        blob_type: BlobType::EventBinary,
        dispatch_type: DispatchType::SendToRoomByName,
        target_type: 0,
        target_id: 0,
        transport_type: 0,
        transport_id: 0,
        cookie: 0,
        payload: Payload::EventSendToRoom {
            room_id: 0x11,
            sender_name: "Bob".into(),
            message: "anyone?".into(),
            extra_data_size: 0,
            sender_id: OTHER,
            hresult: 0,
            chat_type: chat_type::GENERAL,
        },
    };
    apply(&mut communication, &message.build().unwrap());
    let events: Vec<_> = communication.drain_events().collect();
    let CommunicationEvent::Line(line) = &events[0] else {
        panic!("expected a line");
    };
    assert_eq!(line.text, "[General] Bob says, \"anyone?\"");
    assert_eq!(line.log_text_type, 0x1B);
    assert_eq!(line.tag(), "");
}

// -- Files -------------------------------------------------------------------
// Golden behaviour: OpenAC tests/AcDream.Runtime.Tests/Chat/ChatTranscriptLogWriterTests.cs

#[test]
fn log_copies_worded_lines_to_the_file_until_turned_off() {
    let dir = TempDir::new("comm-log");
    let mut communication = Communication::new(CommunicationPaths {
        chat_log_directory: Some(dir.0.clone()),
        journal_directory: None,
    });
    communication.entered_world(PLAYER, "World", "+Acdream", NOW);
    let mut outbox = Outbox::new();
    let speech = |name: &str, text: &str| {
        let mut w = Writer::new();
        w.u32(0x02BB);
        string16l(&mut w, text);
        string16l(&mut w, name);
        w.u32(OTHER);
        w.u32(2);
        w.into_vec()
    };
    apply(&mut communication, &speech("Dww", "before"));
    submit(&mut communication, &mut outbox, "/log session");
    apply(&mut communication, &speech("Dww", "hello"));
    submit(&mut communication, &mut outbox, "/log");
    apply(&mut communication, &speech("Dww", "after"));
    let text = std::fs::read_to_string(dir.0.join("session.txt")).unwrap();
    // Another player's name is written inside its tell tag (OpenAC
    // chat tag fix: the file gets the line as composed).
    assert_eq!(
        text,
        format!(
            "Copying chat to session.txt.  Run command again with no arguments to turn off logging.\n\
             <Tell:IIDString:{OTHER}:Dww>Dww<\\Tell> says, \"hello\"\n"
        )
    );
    let all = lines(&mut communication);
    assert!(all.contains(&"Chat log session.txt closed.".to_owned()));
    assert!(all.contains(&"Chat output now directed only to the screen.".to_owned()));
}

#[test]
fn the_line_is_logged_as_composed_with_its_tags_as_they_arrived() {
    // OpenAC chat tag fix
    // ChatTranscriptLogWriterTests.TheLineIsLoggedAsComposedWithItsTagsAsTheyArrived:
    // a speaker's name inside its tell tag, a server line's tag as it
    // arrived, a plugin's line as shown.
    let dir = TempDir::new("comm-log-tags");
    let mut communication = Communication::new(CommunicationPaths {
        chat_log_directory: Some(dir.0.clone()),
        journal_directory: None,
    });
    communication.entered_world(PLAYER, "World", "+Acdream", NOW);
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "/log session");
    let mut w = Writer::new();
    w.u32(0x02BB);
    string16l(&mut w, "hello");
    string16l(&mut w, "Bob");
    w.u32(OTHER);
    w.u32(2);
    apply(&mut communication, &w.into_vec());
    communication.show_text(
        NOW,
        r"<Tell:IIDString:1:Ann>Ann<\Tell> has logged on.",
        LogTextType::Default,
    );
    communication.show_plugin_text(
        NOW,
        r"<COORD:World:1N, 2E>here<\COORD>",
        LogTextType::Default,
    );
    submit(&mut communication, &mut outbox, "/log");
    let text = std::fs::read_to_string(dir.0.join("session.txt")).unwrap();
    let logged: Vec<&str> = text.lines().skip(1).collect();
    assert_eq!(
        logged,
        [
            format!("<Tell:IIDString:{OTHER}:Bob>Bob<\\Tell> says, \"hello\"").as_str(),
            r"<Tell:IIDString:1:Ann>Ann<\Tell> has logged on.",
            "here",
        ]
    );
}

#[test]
fn without_a_log_directory_log_reports_failure() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "/log session");
    assert_eq!(
        lines(&mut communication),
        ["Failed to redirect to file session!"]
    );
}

#[test]
fn the_journal_loads_on_entry_and_saves_on_exit() {
    let dir = TempDir::new("comm-journal");
    let paths = CommunicationPaths {
        chat_log_directory: None,
        journal_directory: Some(dir.0.clone()),
    };
    let mut communication = Communication::new(paths.clone());
    communication.entered_world(PLAYER, "World", "+Acdream", NOW);
    communication.journal_command(&JournalCommand::NewPage, NOW);
    communication.journal_command(
        &JournalCommand::Edit {
            label: "Notes".into(),
            title: "Holtburg".into(),
            notes: "Buy scarabs.".into(),
        },
        NOW,
    );
    // Each edit is saved as it is made.
    assert!(!communication.journal().is_dirty());
    communication.left_world(NOW);
    assert!(communication.journal().pages().is_empty());

    let mut again = Communication::new(paths);
    again.entered_world(PLAYER, "World", "+Acdream", NOW);
    assert_eq!(again.journal().pages().len(), 1);
    assert_eq!(again.journal().pages()[0].notes, "Buy scarabs.");
}

// -- Session boundary ---------------------------------------------------------

#[test]
fn leaving_the_world_forgets_server_state_but_keeps_the_transcript() {
    let mut communication = owner();
    let mut outbox = Outbox::new();
    rooms_received(&mut communication);
    submit(&mut communication, &mut outbox, "/tell Bob, hi");
    let mut w = Writer::new();
    w.u32(42);
    string16l(&mut w, "Alice");
    string16l(&mut w, "hi");
    apply(
        &mut communication,
        &game_event(GameEventType::CHANNEL_BROADCAST, &w.into_vec()),
    );
    communication.left_world(NOW);
    assert!(!communication.turbine().enabled());
    assert_eq!(communication.targets().last_outgoing_tell_target(), None);
    assert_eq!(communication.chat().len(), 1);
}

#[test]
fn contracts_follow_the_tracker_events() {
    let mut communication = owner();
    let mut w = Writer::new();
    // One entry: header (count 1, one bucket), key, tracker.
    w.u32(0x0001_0001);
    w.u32(0x10);
    w.u32(1);
    w.u32(0x10);
    w.u32(2);
    w.f64(0.0);
    w.f64(0.0);
    apply(
        &mut communication,
        &game_event(
            GameEventType::SEND_CLIENT_CONTRACT_TRACKER_TABLE,
            &w.into_vec(),
        ),
    );
    assert_eq!(communication.contracts().len(), 1);
    let summary = &communication.contract_summaries(NOW)[0];
    assert_eq!(
        (summary.contract_id, summary.progress.as_str()),
        (0x10, "In Progress")
    );

    // OpenAC tests/AcDream.Runtime.Tests/Gameplay/RuntimeContractCatalogTests.cs:
    // with no catalog a contract has its number alone; with one, its name.
    assert_eq!(summary.name, "");
    let mut entry = ac_core::quests::ContractEntry::unknown().clone();
    entry.contract_id = 0x10;
    entry.contract_name = "Tusker Hunt".into();
    entry.description = "Do the thing.".into();
    communication.install_contract_catalog(std::sync::Arc::new(
        ac_core::quests::ContractCatalog::new([(0x10, entry)].into()),
    ));
    assert_eq!(communication.contract_summaries(NOW)[0].name, "Tusker Hunt");
}

#[test]
fn abandoning_a_contract_sends_the_action() {
    // OpenAC `src/AcDream.App/Composition/InteractionRetainedUiComposition.cs`
    // (`AbandonContract` -> `WorldSession.SendAbandonContract`).
    let mut communication = owner();
    let mut outbox = Outbox::new();
    let mut options = crate::character::CharacterOptions::new();
    let mut out = Outbound {
        outbox: &mut outbox,
        player: facts(),
        now_unix_ms: NOW,
        active: true,
        options: &mut options,
    };
    communication.abandon_contract(0x10, &mut out);
    assert_eq!(
        sent(&mut outbox),
        [ac_proto::contract::build_abandon_contract(1, 0x10)]
    );
}

// -- Speech poses -------------------------------------------------------------
// OpenAC tests/AcDream.Runtime.Tests/Chat/LiveChatCommandRouteTests.cs

fn wave_catalog(
    self_text: &str,
    others_text: &str,
    motion: u32,
) -> std::sync::Arc<ChatPoseCatalog> {
    std::sync::Arc::new(ChatPoseCatalog::of([(
        "wave".to_owned(),
        ChatPose {
            motion_command: motion,
            self_text: self_text.to_owned(),
            others_text: others_text.to_owned(),
        },
    )]))
}

#[test]
fn say_consumes_a_dat_pose_and_leaves_an_unknown_token_as_speech() {
    let mut communication = owner();
    communication.install_chat_poses(wave_catalog("wave.", "waves.", 0x1300_0087));
    let mut outbox = Outbox::new();
    submit(
        &mut communication,
        &mut outbox,
        "hello *WAVE* there *not-a-pose*",
    );
    assert_eq!(communication.take_pose_motions(), [0x1300_0087]);
    assert_eq!(
        sent(&mut outbox),
        [
            ac_proto::chat::build_soul_emote(1, "waves.").unwrap(),
            build_talk(2, "hello  there *not-a-pose*").unwrap(),
        ]
    );
    let local = communication.chat().snapshot();
    let local = local.last().unwrap();
    assert_eq!(
        (local.kind, local.sender.as_str(), local.text.as_str()),
        (ChatKind::SoulEmote, "You", "wave.")
    );
}

#[test]
fn say_containing_only_a_pose_sends_no_empty_talk() {
    let mut communication = owner();
    communication.install_chat_poses(wave_catalog("", "", 7));
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, " *wave* ");
    assert_eq!(communication.take_pose_motions(), [7]);
    assert!(sent(&mut outbox).is_empty());
}

#[test]
fn a_pose_line_names_the_speakers_possessive() {
    // OpenAC `ChatRouteParityTests.APoseInsideALineOfSpeechRunsTheSameOnBothClients`
    // ("waves %p hand."), read for a female speaker as the facts default.
    let mut communication = owner();
    communication.install_chat_poses(wave_catalog("You wave.", "waves %p hand.", 0x1300_0026));
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "hello *wave*");
    assert_eq!(
        sent(&mut outbox),
        [
            ac_proto::chat::build_soul_emote(1, "waves her hand.").unwrap(),
            build_talk(2, "hello").unwrap(),
        ]
    );
}

#[test]
fn the_players_own_soul_emote_coming_back_is_not_shown() {
    // Retail drops a heard soul emote whose sender is the player (OpenAC
    // prints it). Another's soul emote is shown.
    let soul_emote = |sender: u32, name: &str, text: &str| {
        let mut w = Writer::new();
        w.u32(0x01E2);
        w.u32(sender);
        string16l(&mut w, name);
        string16l(&mut w, text);
        w.into_vec()
    };
    let mut communication = owner();
    let _ = lines(&mut communication);
    apply(
        &mut communication,
        &soul_emote(PLAYER, "+Acdream", "waves her hand."),
    );
    assert!(lines(&mut communication).is_empty());
    apply(
        &mut communication,
        &soul_emote(OTHER, "+Horan", "waves his hand."),
    );
    let shown = lines(&mut communication);
    assert_eq!(shown.len(), 1);
    assert!(shown[0].contains("waves his hand."), "{shown:?}");
}

#[test]
fn the_session_has_no_poses_until_the_dats_are_read() {
    // `TheSessionCarriesThePoseTableAndIsEmptyUntilItIsRead`.
    let mut communication = owner();
    assert!(communication.chat_poses().is_empty());
    let mut outbox = Outbox::new();
    submit(&mut communication, &mut outbox, "hello *wave*");
    assert!(communication.take_pose_motions().is_empty());
    assert_eq!(sent(&mut outbox), [build_talk(1, "hello *wave*").unwrap()]);
}

// -- Plugin chat filters ------------------------------------------------------
// Golden behaviour: OpenAC tests/AcDream.Core.Tests/Plugins/
// ChatSuppressionFilterTests.cs and tests/AcDream.Runtime.Tests/Gameplay/
// RuntimeCommunicationChatProjectionTests.cs, with the plugins' verdicts
// handed back through `release` (the owner holds what they are asked).

/// Asks a stand-in filter about everything held, as the runner asks the
/// plugins: `suppress` decides per candidate.
fn filter_held(
    communication: &mut Communication,
    mut suppress: impl FnMut(&ac_plugin::abi::ChatLine) -> bool,
) -> Vec<ac_plugin::abi::ChatLine> {
    let mut seen = Vec::new();
    for held in communication.take_held() {
        let candidate = crate::plugins::filter_candidate(&held);
        let drop = suppress(&candidate);
        seen.push(candidate);
        communication.release(held, drop);
    }
    seen
}

#[test]
fn a_suppressed_line_never_reaches_the_transcript_or_its_listeners() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    communication.show_text(NOW, "Someone sells a trade note.", LogTextType::Default);
    communication.show_text(NOW, "Someone waves.", LogTextType::Default);
    assert!(lines(&mut communication).is_empty(), "held, not shown");
    assert_eq!(communication.chat().len(), 0);
    filter_held(&mut communication, |c| c.text.contains("sells"));
    assert_eq!(lines(&mut communication), ["Someone waves."]);
    let kept: Vec<_> = communication
        .chat()
        .entries()
        .map(|e| e.text.clone())
        .collect();
    assert_eq!(kept, ["Someone waves."]);
}

#[test]
fn turning_the_filters_off_keeps_what_waits_and_holds_nothing_new() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    communication.show_text(NOW, "dropped later? no", LogTextType::Default);
    communication.set_plugin_filtering(false);
    assert_eq!(lines(&mut communication), ["dropped later? no"]);
    communication.show_text(NOW, "kept", LogTextType::Default);
    assert_eq!(lines(&mut communication), ["kept"]);
    assert!(communication.take_held().is_empty());
}

#[test]
fn the_filter_candidate_carries_the_text_class_combat_kind_and_arrival_time() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    communication.show_combat_line(
        NOW,
        "You are hit for 7 points of damage.",
        CombatLineKind::Warning,
    );
    communication.show_text(NOW + 1, "Welcome.", LogTextType::Default);
    let seen = filter_held(&mut communication, |_| false);
    assert_eq!(seen.len(), 2);
    assert_eq!((seen[0].kind, seen[0].combat_kind), (8, 2));
    assert_eq!(seen[0].received_unix_ms, NOW);
    assert_eq!(seen[0].sequence, 0, "not kept yet");
    assert_eq!((seen[1].kind, seen[1].combat_kind), (4, 0));
    assert_eq!(seen[1].received_unix_ms, NOW + 1);
}

#[test]
fn a_status_notice_is_offered_under_its_own_kind_and_shown_when_kept() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    communication.show_interface_text(NOW, "You're too busy!");
    assert!(interface_texts(&mut communication).is_empty(), "held");
    let seen = filter_held(&mut communication, |_| false);
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].kind, ac_plugin::abi::types::STATUS_TEXT_KIND);
    assert_eq!(seen[0].text, "You're too busy!");
    assert_eq!(seen[0].log_text_type, LogTextType::ClientLocal.as_u32());
    assert_eq!(interface_texts(&mut communication), ["You're too busy!"]);
    communication.tick(0.0);
    assert_eq!(communication.spew_box().len(), 1);
}

#[test]
fn a_suppressed_status_notice_is_never_shown_and_a_status_filter_leaves_lines_alone() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    communication.show_interface_text(NOW, "You're too busy!");
    communication.show_text(NOW, "A regular line.", LogTextType::Default);
    filter_held(&mut communication, |c| {
        c.kind == ac_plugin::abi::types::STATUS_TEXT_KIND
    });
    let events: Vec<CommunicationEvent> = communication.drain_events().collect();
    assert_eq!(events.len(), 2);
    // Only the plugins hear the dropped notice.
    assert_eq!(
        events[0],
        CommunicationEvent::StatusNotice {
            sequence: 1,
            text: "You're too busy!".into()
        }
    );
    assert!(matches!(&events[1], CommunicationEvent::Line(l) if l.text == "A regular line."));
    communication.tick(0.0);
    assert_eq!(communication.spew_box().len(), 0);
    assert_eq!(communication.chat().len(), 1);
}

// -- Status notices the plugins hear ------------------------------------------
// OpenAC `IEvents.StatusTextReceived`: every notice that takes the status
// path is raised for the plugins, whatever the chat filters decide, in
// order with the transcript's lines.

const BUSY: &str = "The Corpse of War Armoredillo is already in use by Unkle Leo!";

fn transient(communication: &mut Communication, text: &str) {
    let mut w = Writer::new();
    string16l(&mut w, text);
    apply(
        communication,
        &game_event(GameEventType::COMMUNICATION_TRANSIENT_STRING, &w.into_vec()),
    );
}

/// The owner's events, each as `kind: text`.
fn heard(communication: &mut Communication) -> Vec<String> {
    communication
        .drain_events()
        .map(|e| match e {
            CommunicationEvent::Line(line) => format!("line: {}", line.text),
            CommunicationEvent::InterfaceText(text) => format!("shown: {text}"),
            CommunicationEvent::StatusNotice { text, .. } => format!("status: {text}"),
            CommunicationEvent::Confirmation(_) => "confirmation".to_owned(),
        })
        .collect()
}

#[test]
fn a_transient_string_is_a_status_notice_the_plugins_hear() {
    let mut communication = owner();
    communication.show_text(NOW, "Before.", LogTextType::Default);
    transient(&mut communication, BUSY);
    communication.show_text(NOW, "After.", LogTextType::Default);
    assert_eq!(
        heard(&mut communication),
        [
            "line: Before.".to_owned(),
            format!("status: {BUSY}"),
            format!("shown: {BUSY}"),
            "line: After.".to_owned(),
        ]
    );
    assert!(communication.chat().entries().all(|e| e.text != BUSY));
}

#[test]
fn a_status_notice_a_filter_drops_is_still_heard_in_order_with_the_lines() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    communication.show_text(NOW, "Before.", LogTextType::Default);
    transient(&mut communication, BUSY);
    communication.show_text(NOW, "After.", LogTextType::Default);
    assert!(heard(&mut communication).is_empty(), "held");
    filter_held(&mut communication, |c| {
        c.kind == ac_plugin::abi::types::STATUS_TEXT_KIND
    });
    assert_eq!(
        heard(&mut communication),
        [
            "line: Before.".to_owned(),
            format!("status: {BUSY}"),
            "line: After.".to_owned(),
        ]
    );
    communication.tick(0.0);
    assert_eq!(communication.spew_box().len(), 0, "never shown");
}

/// A dropped tell never reached the transcript, so it never sets the reply
/// target (OpenAC `ChatCommandTargetState` learns from `EntryAppended`).
#[test]
fn a_dropped_tell_never_sets_the_reply_target() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    let mut w = Writer::new();
    string16l(&mut w, "buy my stuff");
    string16l(&mut w, "Spammer");
    w.u32(OTHER);
    w.u32(PLAYER);
    w.u32(LogTextType::Tell.as_u32());
    apply(
        &mut communication,
        &game_event(GameEventType::TELL, &w.into_vec()),
    );
    filter_held(&mut communication, |c| c.sender == "Spammer");
    assert!(lines(&mut communication).is_empty());
    assert_eq!(communication.targets().last_incoming_tell_sender(), None);
}

// -- What a plugin is handed with each line -------------------------------------
// Golden values: OpenAC (origin/main) tests/AcDream.Runtime.Tests/Plugins/
// PluginChatChannelLineTests.cs. The channel number and the printed line
// come with the words, the speaker apart; the filter is offered the same
// line before the language filter, a reader the line as printed.

/// The plugin lines the owner's events carry.
fn plugin_lines(communication: &mut Communication) -> Vec<ac_plugin::abi::ChatLine> {
    communication
        .drain_events()
        .filter_map(|e| match e {
            CommunicationEvent::Line(line) => Some(crate::plugins::plugin_chat_line(&line)),
            CommunicationEvent::InterfaceText(_)
            | CommunicationEvent::StatusNotice { .. }
            | CommunicationEvent::Confirmation(_) => None,
        })
        .collect()
}

fn channel_broadcast(communication: &mut Communication, channel_id: u32, sender: &str, text: &str) {
    let mut w = Writer::new();
    w.u32(channel_id);
    string16l(&mut w, sender);
    string16l(&mut w, text);
    apply(
        communication,
        &game_event(GameEventType::CHANNEL_BROADCAST, &w.into_vec()),
    );
}

fn tell_from(communication: &mut Communication, sender: &str, text: &str, sender_guid: u32) {
    let mut w = Writer::new();
    string16l(&mut w, text);
    string16l(&mut w, sender);
    w.u32(sender_guid);
    w.u32(PLAYER);
    w.u32(LogTextType::Tell.as_u32());
    apply(
        communication,
        &game_event(GameEventType::TELL, &w.into_vec()),
    );
}

#[test]
fn a_numbered_channel_line_reaches_the_plugin_with_its_number_and_printed_line() {
    for (channel_id, printed) in [
        (
            0x1000,
            "Your patron <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"",
        ),
        (
            0x2000,
            "Your vassal <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"",
        ),
        (
            0x4000,
            "Your follower <Tell:IIDString:0:Bob>Bob<\\Tell> says to you, \"hi\"",
        ),
        (
            0x100_0000,
            "[Co-Vassals] <Tell:IIDString:0:Bob>Bob<\\Tell> says, \"hi\"",
        ),
        (
            0x200_0000,
            "[Allegiance Broadcast] <Tell:IIDString:0:Bob>Bob<\\Tell> says, \"hi\"",
        ),
        (
            0x8,
            "<Tell:IIDString:0:Bob>Bob<\\Tell> says on the Advocate 1 channel, \"hi\"",
        ),
    ] {
        let mut communication = owner();
        channel_broadcast(&mut communication, channel_id, "Bob", "hi");
        let seen = plugin_lines(&mut communication);
        assert_eq!(seen.len(), 1, "{channel_id:#x}");
        assert_eq!(seen[0].channel_id, channel_id);
        assert_eq!(seen[0].display_text, printed);
        assert_eq!(seen[0].sender, "Bob");
        assert_eq!(seen[0].text, "hi");
    }
}

// The entry's number holds a system line's text class; it is no channel.
#[test]
fn a_line_that_is_no_channel_line_carries_no_channel_number() {
    let mut communication = owner();
    communication.show_text(
        NOW,
        "You have entered the Allegiance channel.",
        LogTextType::Help,
    );
    let seen = plugin_lines(&mut communication);
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].channel_id, 0);
    assert_eq!(
        seen[0].display_text,
        "You have entered the Allegiance channel."
    );
}

#[test]
fn a_filter_sees_the_same_channel_number_and_printed_line_as_a_reader() {
    let mut communication = owner();
    communication.set_plugin_filtering(true);
    channel_broadcast(&mut communication, 0x2000, "Bob", "hi");
    let offered = filter_held(&mut communication, |_| false);
    let received = plugin_lines(&mut communication);
    assert_eq!((offered.len(), received.len()), (1, 1));
    assert_eq!(offered[0].channel_id, 0x2000);
    assert_eq!(received[0].channel_id, offered[0].channel_id);
    assert_eq!(received[0].display_text, offered[0].display_text);
    assert!(offered[0].display_text.starts_with("Your vassal "));
}

// Mutation check (2026-10-13): censoring before the filters are asked
// turns this red.
#[test]
fn a_filter_sees_the_line_before_censoring_and_a_reader_sees_it_as_printed() {
    let mut communication = owner();
    communication.set_filter_language(true);
    communication.set_filter_language_patterns(Some(vec!["zork".to_owned()]));
    communication.set_plugin_filtering(true);
    tell_from(&mut communication, "Bob", "a zork b", 0x5000_0077);
    let offered = filter_held(&mut communication, |_| false);
    let received = plugin_lines(&mut communication);
    assert_eq!(offered[0].text, "a zork b");
    assert!(offered[0].display_text.ends_with("tells you, \"a zork b\""));
    assert_eq!(received[0].text, "a **** b");
    assert!(
        received[0]
            .display_text
            .ends_with("tells you, \"a **** b\"")
    );
}

#[test]
fn the_printed_line_is_the_chat_windows_line() {
    let mut communication = owner();
    tell_from(&mut communication, "Bob", "hello", 0x5000_0077);
    let seen = plugin_lines(&mut communication);
    let entry = communication.chat().entries().last().unwrap().clone();
    assert_eq!(seen[0].display_text, super::feed::format_tagged(&entry));
    assert_eq!(
        seen[0].display_text,
        "<Tell:IIDString:1342177399:Bob>Bob<\\Tell> tells you, \"hello\""
    );
}

// OpenAC `PluginChatMessage.Text` and `DisplayText` carry no timestamp:
// with the player's chat timestamps on, the window's line gains its
// `H:mm:ss ` prefix, and a plugin still reads the words alone (and the
// printed line without the stamp), so a pattern matcher keeps matching.
#[test]
fn a_plugin_reads_the_words_alone_while_chat_timestamps_are_on() {
    let mut communication = owner();
    tell_from(&mut communication, "Bob", "hello", 0x5000_0077);
    let entry = communication.chat().entries().last().unwrap().clone();
    let shown = ChatLine::from_entry(&entry, true, 0);
    assert_ne!(shown.text, super::feed::format(&entry), "stamped");
    let line = crate::plugins::plugin_chat_line(&shown);
    assert_eq!(line.text, "hello");
    assert_eq!(line.display_text, super::feed::format_tagged(&entry));
}
