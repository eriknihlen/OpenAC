//! Routing from the wire to the owners, through the one entry every server
//! message takes (`SessionState::apply`), as OpenAC's
//! `tests/AcDream.Runtime.Tests/Session/LiveSessionEventRouterTests.cs` drives
//! its router. The router's delegate bookkeeping (attach and detach,
//! nested routers, throwing sinks, subscription sets and binding
//! declarations) has no counterpart: here routing is one match per owner
//! and nothing subscribes.

use std::time::Duration;

use ac_proto::fellowship::{FellowMember, FellowshipFullUpdate};
use ac_proto::turbine_chat::{BlobType, DispatchType, Payload, TurbineChat};
use ac_proto::{GameEventType, Inbound, PrivateUpdateAttribute};

use super::{Clocks, Outbox, SessionState};
use crate::communication::CommunicationPaths;
use crate::world::tests::{empty_description, game_event};

const SELF: u32 = 0x5000_000A;
const OTHER: u32 = 0x5000_0002;
const THIRD: u32 = 0x5000_0003;

const CLOCKS: Clocks = Clocks {
    wall_unix_ms: 1_790_000_000_000,
    session: Duration::from_secs(30),
};

fn in_world() -> (SessionState, Outbox) {
    let mut state = SessionState::new(CommunicationPaths::default());
    state.world.set_player(SELF);
    state
        .communication
        .entered_world(SELF, "Router", "Self", CLOCKS.wall_unix_ms);
    (state, Outbox::new())
}

fn apply(state: &mut SessionState, outbox: &mut Outbox, message: &Inbound<'_>) {
    state.apply(message, outbox, true, CLOCKS);
}

fn texts(state: &SessionState) -> Vec<String> {
    state
        .communication
        .transcript_lines()
        .into_iter()
        .map(|l| l.text)
        .collect()
}

#[test]
fn a_server_message_is_routed_by_its_wire_chat_type() {
    // ServerMessage_RoutesThroughAddText_WithWireChatTypeVerbatim_WhenWired.
    let (mut state, mut outbox) = in_world();
    let before = state.communication.transcript_lines().len();
    apply(
        &mut state,
        &mut outbox,
        &Inbound::ServerMessage(ac_proto::chat::ServerMessage {
            message: "You are too encumbered to carry that!".into(),
            chat_type: 0x1A,
        }),
    );
    // 0x1A is the client-local type: the text goes to the interface (the
    // spew box), not the chat, as OpenAC's AddText route does.
    assert_eq!(state.communication.transcript_lines().len(), before);
    assert!(state.drain_host_events().iter().any(|e| matches!(
        e,
        super::HostEvent::InterfaceText(t) if t == "You are too encumbered to carry that!"
    )));

    // ServerMessage_FallsBackToChatDirectly_WhenNoRouterWired: any other
    // type is a chat line of that type.
    apply(
        &mut state,
        &mut outbox,
        &Inbound::ServerMessage(ac_proto::chat::ServerMessage {
            message: "fallback path".into(),
            chat_type: 0x00,
        }),
    );
    let lines = state.communication.transcript_lines();
    assert_eq!(lines.len(), before + 1);
    let line = lines.last().unwrap();
    assert_eq!(line.text, "fallback path");
    assert_eq!(line.log_text_type, 0x00);
}

#[test]
fn repeated_server_messages_publish_distinct_entries() {
    // RepeatedServerMessagePackets_PublishDistinctEntriesThroughAddText
    // (OpenAC v0.1.25, #198): CombatSelf, CombatEnemy, Magic and
    // Default each keep both copies with their own sequence numbers.
    for chat_type in [0x16, 0x15, 0x07, 0x00] {
        let (mut state, mut outbox) = in_world();
        let before = state.communication.transcript_lines();
        for _ in 0..2 {
            apply(
                &mut state,
                &mut outbox,
                &Inbound::ServerMessage(ac_proto::chat::ServerMessage {
                    message: "Additional damage: 5".into(),
                    chat_type,
                }),
            );
        }
        let lines = state.communication.transcript_lines();
        assert_eq!(lines.len(), before.len() + 2, "type {chat_type:#x}");
        let new = &lines[before.len()..];
        assert!(
            new.iter()
                .all(|l| l.text == "Additional damage: 5" && l.log_text_type == chat_type)
        );
        assert_eq!(new[1].sequence, new[0].sequence + 1);
    }
}

#[test]
fn a_death_line_naming_the_local_player_is_not_echoed_but_a_bystanders_is() {
    // PlayerKilled_SuppressesLocalParticipantsAndKeepsBystanderLine.
    let (mut state, mut outbox) = in_world();
    let before = state.communication.transcript_lines().len();
    for (text, victim, killer) in [
        ("self died", SELF, OTHER),
        ("self killed", OTHER, SELF),
        ("bystander", OTHER, THIRD),
    ] {
        apply(
            &mut state,
            &mut outbox,
            &Inbound::PlayerKilled(ac_proto::combat::PlayerKilled {
                death_message: text.into(),
                victim_guid: victim,
                killer_guid: killer,
            }),
        );
    }
    let lines = state.communication.transcript_lines();
    assert_eq!(lines.len(), before + 1);
    let line = lines.last().unwrap();
    assert_eq!(line.text, "bystander");
    assert_eq!(line.sender_guid, OTHER);
}

fn turbine(blob_type: BlobType, dispatch_type: DispatchType, payload: Payload) -> Inbound<'static> {
    Inbound::TurbineChat(Box::new(TurbineChat {
        blob_type,
        dispatch_type,
        target_type: 0,
        target_id: 0,
        transport_type: 0,
        transport_id: 0,
        cookie: 0,
        payload,
    }))
}

fn response(hresult: i32) -> Inbound<'static> {
    turbine(
        BlobType::ResponseBinary,
        DispatchType::Unknown,
        Payload::Response {
            context_id: 5,
            response_id: 2,
            method_id: 2,
            hresult,
        },
    )
}

#[test]
fn a_rejected_turbine_chat_send_is_said_in_the_chat() {
    // TurbineChat_ResponseWithNonZeroHResult_SurfacesAsSystemMessage.
    let (mut state, mut outbox) = in_world();
    let before = texts(&state).len();
    apply(&mut state, &mut outbox, &response(-1));
    let texts = texts(&state);
    assert_eq!(texts.len(), before + 1);
    assert!(texts.last().unwrap().to_lowercase().contains("rejected"));
}

#[test]
fn an_accepted_turbine_chat_send_stays_silent() {
    // TurbineChat_ResponseWithZeroHResult_StaysSilent.
    let (mut state, mut outbox) = in_world();
    let before = texts(&state).len();
    apply(&mut state, &mut outbox, &response(0));
    assert_eq!(texts(&state).len(), before);
}

#[test]
fn a_turbine_chat_room_message_is_a_channel_line() {
    // TurbineChat_EventSendToRoom_StillRoutesToChannelBroadcast.
    let (mut state, mut outbox) = in_world();
    let before = texts(&state).len();
    apply(
        &mut state,
        &mut outbox,
        &turbine(
            BlobType::EventBinary,
            DispatchType::SendToRoomByName,
            Payload::EventSendToRoom {
                room_id: 2,
                sender_name: "Someone".into(),
                message: "hi".into(),
                extra_data_size: 0x0C,
                sender_id: 0x5000_0001,
                hresult: 0,
                chat_type: 2,
            },
        ),
    );
    let texts = texts(&state);
    assert_eq!(texts.len(), before + 1);
    assert!(texts.last().unwrap().contains("hi"));
}

fn fellow(guid: u32, name: &str) -> FellowMember {
    FellowMember {
        guid,
        cp_cache: 0,
        lum_cache: 0,
        level: 1,
        max_health: 100,
        max_stamina: 100,
        max_mana: 100,
        current_health: 100,
        current_stamina: 100,
        current_mana: 100,
        share_loot: 0,
        name: name.into(),
    }
}

#[test]
fn a_fellowship_quit_removes_another_member_or_clears_on_the_players_own() {
    // FellowshipQuit_RoutesSelfGuidToClearAndOtherGuidToRemove.
    let (mut state, mut outbox) = in_world();
    state
        .gameplay
        .fellowship_mut()
        .apply_full_update(&FellowshipFullUpdate {
            members: vec![fellow(SELF, "Self"), fellow(OTHER, "Other")],
            name: "The Fellows".into(),
            leader_guid: SELF,
            share_xp: true,
            even_xp_split: false,
            open_fellow: true,
            locked: false,
            departed: Vec::new(),
        });

    let quit = game_event(GameEventType::FELLOWSHIP_QUIT, &[OTHER]);
    apply(&mut state, &mut outbox, &Inbound::parse(&quit).unwrap());
    let fellowship = state.gameplay.fellowship();
    assert!(fellowship.is_in_fellowship());
    assert_eq!(fellowship.member_count(), 1);
    assert!(fellowship.member(OTHER).is_none());
    assert!(fellowship.member(SELF).is_some());

    let quit = game_event(GameEventType::FELLOWSHIP_QUIT, &[SELF]);
    apply(&mut state, &mut outbox, &Inbound::parse(&quit).unwrap());
    let fellowship = state.gameplay.fellowship();
    assert!(!fellowship.is_in_fellowship());
    assert_eq!(fellowship.member_count(), 0);

    // OpenAC v0.1.25 (`FellowshipParityTests`): each departure
    // reaches the shared chat as a default-class line.
    let lines = state.communication.transcript_lines();
    let said: Vec<(&str, u32)> = lines
        .iter()
        .rev()
        .take(2)
        .rev()
        .map(|l| (l.text.as_str(), l.log_text_type))
        .collect();
    assert_eq!(
        said,
        [
            ("Other has left your Fellowship.", 0),
            (
                "You are no longer a member of the The Fellows Fellowship.",
                0
            )
        ]
    );
}

#[test]
fn an_attribute_update_reaches_the_players_numbers() {
    // AttributeAndSkillUpdates_RouteToPlayerStateAndMovementSeam (the
    // player-state half: Quickness 100 start + 60 ranks).
    let (mut state, mut outbox) = in_world();
    state.world.apply_player_description(&empty_description());
    apply(
        &mut state,
        &mut outbox,
        &Inbound::PrivateUpdateAttribute(PrivateUpdateAttribute {
            sequence: 1,
            attribute: 3,
            ranks: 60,
            start: 100,
            xp: 500,
        }),
    );
    let quickness = state
        .world
        .player()
        .unwrap()
        .numbers()
        .get_attribute(ac_core::player::AttributeKind::Quickness)
        .unwrap();
    assert_eq!(quickness.ranks + quickness.start, 160);
    assert_eq!(quickness.xp, 500);
}

#[test]
fn a_message_before_the_world_is_entered_reaches_no_player_state() {
    // The router publishes nothing to a player that is not there yet
    // (CreatedRouterPublishesNoHandlersUntilExplicitAttach, in its Rust
    // form: a private update with no description changes nothing).
    let mut state = SessionState::new(CommunicationPaths::default());
    let mut outbox = Outbox::new();
    state.apply(
        &Inbound::PrivateUpdateAttribute(PrivateUpdateAttribute {
            sequence: 1,
            attribute: 3,
            ranks: 60,
            start: 100,
            xp: 500,
        }),
        &mut outbox,
        false,
        CLOCKS,
    );
    assert!(state.world.player().is_none());
    assert_eq!(outbox.drain().count(), 0);
}

// -- Chess ------------------------------------------------------------------

const BOARD: u32 = 0x7A9B_4001;

fn chess_event(
    state: &mut SessionState,
    outbox: &mut Outbox,
    event: GameEventType,
    values: &[i32],
) {
    let mut payload = vec![BOARD];
    payload.extend(values.iter().map(|v| v.cast_unsigned()));
    let body = game_event(event, &payload);
    apply(state, outbox, &Inbound::parse(&body).unwrap());
}

/// A chess board out in the world, someone else's.
fn place_board(state: &mut SessionState) {
    use ac_core::items::{ItemType, PublicWeenieFlags};
    use ac_proto::{CreateObject, ModelData, PhysicsTimestamps, PublicWeenieDesc};
    let desc = PublicWeenieDesc {
        name: "Chess Board".into(),
        item_type: ItemType::GAMEBOARD.bits(),
        object_description_flags: PublicWeenieFlags::STUCK.bits(),
        ..PublicWeenieDesc::default()
    };
    let create = Inbound::CreateObject(Box::new(CreateObject {
        guid: BOARD,
        model: ModelData::default(),
        physics: crate::world::tests::physics(
            PhysicsTimestamps {
                instance: 1,
                ..PhysicsTimestamps::default()
            },
            None,
        ),
        weenie: Some(desc),
    }));
    state.world.apply(&create);
}

fn joins(outbox: &mut Outbox) -> Vec<Vec<u8>> {
    outbox
        .drain()
        .map(|o| o.body)
        .filter(|b| b.get(8..12) == Some(&0x0269u32.to_le_bytes()[..]))
        .collect()
}

fn use_board(
    state: &mut SessionState,
    outbox: &mut Outbox,
    command: crate::inventory::ItemCommand,
) {
    state.command(super::SessionCommand::Item(command), outbox, true, CLOCKS);
}

/// How many times the game window was asked to show, and the status-line
/// texts, among the host events.
fn board_events(state: &mut SessionState) -> (usize, Vec<String>) {
    let events = state.drain_host_events();
    let shown = events
        .iter()
        .filter(|e| **e == super::HostEvent::BeginGame(BOARD))
        .count();
    let status = events
        .into_iter()
        .filter_map(|e| match e {
            super::HostEvent::InterfaceText(text) => Some(text),
            _ => None,
        })
        .collect();
    (shown, status)
}

/// [`use_board`] `after` past the test clock (the use pacing reads it).
fn use_board_after(
    state: &mut SessionState,
    outbox: &mut Outbox,
    command: crate::inventory::ItemCommand,
    after: Duration,
) {
    let clocks = Clocks {
        wall_unix_ms: CLOCKS.wall_unix_ms + i64::try_from(after.as_millis()).unwrap(),
        session: CLOCKS.session + after,
    };
    state.command(super::SessionCommand::Item(command), outbox, true, clocks);
}

#[test]
fn chess_events_route_into_the_session_owner() {
    // LiveSessionEventRouterTests.ChessEvents_RouteIntoTheSharedRuntimeOwner
    // and GameEventWiringTests.WireAll_ChessEvents_ReachTheirCallbacks.
    use crate::gameplay::{ChessCommand, ChessStage, GameplayCommand};
    use ac_core::chess::ChessSquare;
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    use_board(
        &mut state,
        &mut outbox,
        crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(BOARD)),
    );
    let stage = |s: &SessionState| s.gameplay.chess().stage();

    chess_event(
        &mut state,
        &mut outbox,
        GameEventType::JOIN_GAME_RESPONSE,
        &[1],
    );
    chess_event(&mut state, &mut outbox, GameEventType::START_GAME, &[0]);
    assert_eq!(stage(&state), ChessStage::OpponentsTurn);

    chess_event(
        &mut state,
        &mut outbox,
        GameEventType::OPPONENT_TURN,
        &[0, 5, 0x5000_0002, 4, 1, 4, 3],
    );
    assert_eq!(stage(&state), ChessStage::MyTurn);

    outbox.drain().count();
    state.command(
        super::SessionCommand::Gameplay(GameplayCommand::Chess(ChessCommand::Move {
            from: ChessSquare::new(4, 6),
            to: ChessSquare::new(4, 4),
        })),
        &mut outbox,
        true,
        CLOCKS,
    );
    let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0][8..12], 0x026Bu32.to_le_bytes());
    chess_event(&mut state, &mut outbox, GameEventType::MOVE_RESPONSE, &[1]);
    assert_eq!(stage(&state), ChessStage::OpponentsTurn);

    chess_event(
        &mut state,
        &mut outbox,
        GameEventType::OPPONENT_STALEMATE,
        &[1, 1],
    );
    assert!(
        texts(&state)
            .iter()
            .any(|t| t.contains("declared a stalemate"))
    );

    chess_event(&mut state, &mut outbox, GameEventType::GAME_OVER, &[0]);
    assert_eq!(stage(&state), ChessStage::NotPlaying);
    assert!(
        texts(&state)
            .iter()
            .any(|t| t == "You have been defeated!  To join a game, select and use the game board."),
        "{:?}",
        texts(&state)
    );
}

#[test]
fn using_a_board_with_no_window_sends_the_join_and_waits_for_the_answer() {
    // RuntimeChessBeginGameTests.UsingABoardWithNoWindow_SendsTheJoinAndWaitsForTheAnswer
    // and RuntimeChessStateTests.GameRuntime_OwnsTheChessStateAndSendsItsLinesToChat.
    use crate::gameplay::ChessStage;
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    use_board(
        &mut state,
        &mut outbox,
        crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(BOARD)),
    );
    let joins = joins(&mut outbox);
    assert_eq!(joins.len(), 1);
    assert_eq!(joins[0][12..16], BOARD.to_le_bytes());
    assert_eq!(state.gameplay.chess().stage(), ChessStage::Joining);
    assert_eq!(state.gameplay.chess().board_guid(), BOARD);
    assert!(
        texts(&state)
            .iter()
            .any(|t| t.contains("Attempting to join game"))
    );
    assert!(
        state
            .drain_host_events()
            .contains(&super::HostEvent::BeginGame(BOARD))
    );
}

#[test]
fn using_the_same_board_again_sends_no_second_join() {
    // RuntimeChessBeginGameTests.UsingTheSameBoardAgain_SendsNoSecondJoin.
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    // A second later, past the use pacing.
    for after in [Duration::ZERO, Duration::from_secs(1)] {
        use_board_after(
            &mut state,
            &mut outbox,
            crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(BOARD)),
            after,
        );
    }
    assert_eq!(joins(&mut outbox).len(), 1);
    // The window shows for each use; the second says so.
    let (shown, status) = board_events(&mut state);
    assert_eq!(shown, 2);
    assert!(
        status
            .iter()
            .any(|t| t == "You are already playing this game."),
        "{status:?}"
    );
}

#[test]
fn an_automation_use_of_a_board_joins_its_game_instead_of_using_it() {
    // RuntimeChessBeginGameTests.AnAutomationUseOfABoard_JoinsItsGameInsteadOfUsingIt.
    use crate::gameplay::ChessStage;
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    use_board(
        &mut state,
        &mut outbox,
        crate::inventory::ItemCommand::UseWorldObject(crate::inventory::ObjectRef::Guid(BOARD)),
    );
    let sent: Vec<Vec<u8>> = outbox.drain().map(|o| o.body).collect();
    assert_eq!(sent.len(), 1, "only the join goes out");
    assert_eq!(sent[0][8..12], 0x0269u32.to_le_bytes());
    assert_eq!(state.gameplay.chess().stage(), ChessStage::Joining);
}

#[test]
fn leaving_the_world_ends_the_game() {
    // RuntimeGenerationReset's chess stage: the session reset ends any game.
    use crate::gameplay::ChessStage;
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    use_board(
        &mut state,
        &mut outbox,
        crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(BOARD)),
    );
    let mut world = std::mem::take(&mut state.world);
    state.gameplay.left_world(&mut world);
    assert_eq!(state.gameplay.chess().stage(), ChessStage::NotPlaying);
    assert_eq!(state.gameplay.chess().board_guid(), 0);
}

#[test]
fn a_double_click_on_a_board_within_the_use_pacing_is_swallowed() {
    // OpenAC RuntimeItemInteraction.ActivateItem: the use pacing (200 ms)
    // comes before the board's primary use, so the second click of a
    // double-click neither shows the window again nor says anything.
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    for after in [Duration::ZERO, Duration::from_millis(100)] {
        use_board_after(
            &mut state,
            &mut outbox,
            crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(BOARD)),
            after,
        );
    }
    assert_eq!(joins(&mut outbox).len(), 1);
    let shown = state
        .drain_host_events()
        .into_iter()
        .filter(|e| *e == super::HostEvent::BeginGame(BOARD))
        .count();
    assert_eq!(shown, 1);
    assert!(
        !texts(&state).iter().any(|t| t.contains("already playing")),
        "{:?}",
        texts(&state)
    );
}

/// Run a chess command through the session; the bodies it sent.
fn chess(
    state: &mut SessionState,
    outbox: &mut Outbox,
    command: crate::gameplay::ChessCommand,
) -> Vec<Vec<u8>> {
    outbox.drain().count();
    state.command(
        super::SessionCommand::Gameplay(crate::gameplay::GameplayCommand::Chess(command)),
        outbox,
        true,
        CLOCKS,
    );
    outbox.drain().map(|o| o.body).collect()
}

/// The action type and the words after it.
fn action(body: &[u8]) -> (u32, Vec<u32>) {
    let words: Vec<u32> = body
        .chunks_exact(4)
        .map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]]))
        .collect();
    (words[2], words[3..].to_vec())
}

/// In the world at the board, White, White to move.
fn seated_white() -> (SessionState, Outbox) {
    let (mut state, mut outbox) = in_world();
    place_board(&mut state);
    use_board(
        &mut state,
        &mut outbox,
        crate::inventory::ItemCommand::Use(crate::inventory::ObjectRef::Guid(BOARD)),
    );
    chess_event(
        &mut state,
        &mut outbox,
        GameEventType::JOIN_GAME_RESPONSE,
        &[0],
    );
    chess_event(&mut state, &mut outbox, GameEventType::START_GAME, &[0]);
    state.drain_host_events();
    (state, outbox)
}

fn status_texts(state: &mut SessionState) -> Vec<String> {
    state
        .drain_host_events()
        .into_iter()
        .filter_map(|e| match e {
            super::HostEvent::InterfaceText(text) => Some(text),
            _ => None,
        })
        .collect()
}

#[test]
fn a_move_the_rules_refuse_sends_nothing_and_says_why() {
    // OpenAC MiniGameUiController.ClickSquare: a move goes out only when the
    // board accepts it; RuntimeChessState.TryMove says the refusal.
    use crate::gameplay::ChessCommand;
    use ac_core::chess::ChessSquare;
    let (mut state, mut outbox) = seated_white();
    let sent = chess(
        &mut state,
        &mut outbox,
        ChessCommand::Move {
            from: ChessSquare::new(4, 1),
            to: ChessSquare::new(4, 4),
        },
    );
    assert!(sent.is_empty(), "{sent:?}");
    assert!(texts(&state).iter().any(|t| t
        == "The selected piece cannot move that direction, try again.  It is your turn to move."));

    let sent = chess(
        &mut state,
        &mut outbox,
        ChessCommand::Move {
            from: ChessSquare::new(4, 1),
            to: ChessSquare::new(4, 3),
        },
    );
    assert_eq!(sent.len(), 1);
    assert_eq!(action(&sent[0]), (0x026B, vec![4, 1, 4, 3]));
}

#[test]
fn stalemate_sends_the_offer_then_the_retraction_and_nothing_outside_a_game() {
    use crate::gameplay::ChessCommand;
    let (mut state, mut outbox) = seated_white();
    let first = chess(&mut state, &mut outbox, ChessCommand::ToggleStalemate);
    let second = chess(&mut state, &mut outbox, ChessCommand::ToggleStalemate);
    assert_eq!(
        first.iter().map(|b| action(b)).collect::<Vec<_>>(),
        [(0x026E, vec![1])]
    );
    assert_eq!(
        second.iter().map(|b| action(b)).collect::<Vec<_>>(),
        [(0x026E, vec![0])]
    );

    let (mut state, mut outbox) = in_world();
    assert!(chess(&mut state, &mut outbox, ChessCommand::ToggleStalemate).is_empty());
    assert_eq!(
        status_texts(&mut state),
        [crate::gameplay::chess::NOT_PLAYING_TEXT]
    );
}

#[test]
fn pass_goes_out_only_in_a_game() {
    use crate::gameplay::ChessCommand;
    let (mut state, mut outbox) = seated_white();
    let sent = chess(&mut state, &mut outbox, ChessCommand::Pass);
    assert_eq!(
        sent.iter().map(|b| action(b)).collect::<Vec<_>>(),
        [(0x026D, vec![])]
    );

    let (mut state, mut outbox) = in_world();
    assert!(chess(&mut state, &mut outbox, ChessCommand::Pass).is_empty());
    assert_eq!(
        status_texts(&mut state),
        [crate::gameplay::chess::NOT_PLAYING_TEXT]
    );
}

#[test]
fn resign_sends_the_quit_whether_or_not_a_game_is_on_and_ends_the_game() {
    // OpenAC MiniGameUiController.OnResign: on a yes the quit goes out, then
    // the game ends (RuntimeChessState.Resign, which does nothing outside a
    // game).
    use crate::gameplay::{ChessCommand, ChessStage};
    let (mut state, mut outbox) = in_world();
    let sent = chess(&mut state, &mut outbox, ChessCommand::Resign);
    assert_eq!(
        sent.iter().map(|b| action(b)).collect::<Vec<_>>(),
        [(0x026A, vec![])]
    );
    assert_eq!(state.gameplay.chess().view().games_ended, 0);

    let (mut state, mut outbox) = seated_white();
    let sent = chess(&mut state, &mut outbox, ChessCommand::Resign);
    assert_eq!(
        sent.iter().map(|b| action(b)).collect::<Vec<_>>(),
        [(0x026A, vec![])]
    );
    assert_eq!(state.gameplay.chess().stage(), ChessStage::NotPlaying);
    assert_eq!(state.gameplay.chess().view().games_ended, 1);
    assert_eq!(
        texts(&state).last().map(String::as_str),
        Some(crate::gameplay::chess::JOIN_PROMPT)
    );
}
