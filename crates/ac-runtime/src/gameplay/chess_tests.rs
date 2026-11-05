//! Cases ported from OpenAC
//! `tests/AcDream.Runtime.Tests/Gameplay/RuntimeChessStateTests.cs`.

use ac_core::chess::{ChessPiece, ChessPieceType};
use ac_proto::chess::ChessMove;

use super::*;

const BOARD: u32 = 0x7A9B_4001;
const OTHER_BOARD: u32 = 0x7A9B_4002;

type R = ChessMoveResult;

fn sq(name: &str) -> ChessSquare {
    let b = name.as_bytes();
    ChessSquare::new(
        i32::from(b[0]) - i32::from(b'a'),
        i32::from(b[1]) - i32::from(b'1'),
    )
}

fn join(board: u32, team: i32) -> JoinGameResponse {
    JoinGameResponse {
        board_guid: board,
        team,
    }
}

fn start(first_team: i32) -> StartGame {
    StartGame {
        board_guid: BOARD,
        first_team,
    }
}

fn response(result: ChessMoveResult) -> MoveResponse {
    MoveResponse {
        board_guid: BOARD,
        result: result.0,
    }
}

fn over(board: u32, winner: i32) -> GameOver {
    GameOver {
        board_guid: board,
        winner,
    }
}

fn opponent_move(team: i32, from: &str, to: &str) -> OpponentTurn {
    let (from, to) = (sq(from), sq(to));
    OpponentTurn {
        board_guid: BOARD,
        team,
        chess_move: ChessMove {
            move_type: ChessMoveType::FROM_TO,
            player_guid: 0x5000_0002,
            from_x: from.x,
            from_y: from.y,
            to_x: to.x,
            to_y: to.y,
            piece_guid: 0,
        },
    }
}

fn lines(chess: &mut ChessState) -> Vec<(String, LogTextType)> {
    chess.drain_lines().collect()
}

fn last_line(chess: &mut ChessState) -> String {
    lines(chess).pop().map(|(t, _)| t).unwrap_or_default()
}

/// Begins, joins as `team` and starts with White to move.
fn seated(team: ChessTeam) -> ChessState {
    let mut chess = ChessState::default();
    chess.begin(BOARD);
    chess.apply_join_response(&join(BOARD, team.wire()));
    chess.apply_start_game(&start(ChessTeam::White.wire()));
    lines(&mut chess);
    chess
}

#[test]
fn begin_asks_to_join_and_says_so() {
    let mut chess = ChessState::default();
    assert!(chess.begin(BOARD));
    assert_eq!(chess.stage(), ChessStage::Joining);
    assert_eq!(chess.board_guid(), BOARD);
    assert_eq!(
        lines(&mut chess),
        [(
            "Attempting to join game, please wait...".to_owned(),
            LogTextType::Default
        )]
    );
}

#[test]
fn begin_while_playing_is_refused_with_a_message() {
    let mut chess = ChessState::default();
    chess.begin(BOARD);
    lines(&mut chess);
    assert!(!chess.begin(BOARD));
    assert_eq!(
        lines(&mut chess),
        [(
            "You are already playing this game.".to_owned(),
            LogTextType::ClientLocal
        )]
    );
    assert!(!chess.begin(OTHER_BOARD));
    assert_eq!(
        lines(&mut chess),
        [(
            "You are already playing another game.".to_owned(),
            LogTextType::ClientLocal
        )]
    );
    assert_eq!(chess.board_guid(), BOARD);
}

#[test]
fn join_refused_goes_back_to_not_playing() {
    let mut chess = ChessState::default();
    chess.begin(BOARD);
    chess.apply_join_response(&join(BOARD, -1));
    assert_eq!(chess.stage(), ChessStage::NotPlaying);
    assert_eq!(chess.board_guid(), 0);
    assert_eq!(last_line(&mut chess), "You could not join that game.");
    assert!(chess.begin(BOARD));
}

#[test]
fn join_and_start_tell_the_team_whether_it_moves_first() {
    let mut white = ChessState::default();
    white.begin(BOARD);
    white.apply_join_response(&join(BOARD, 0));
    assert_eq!(white.stage(), ChessStage::WaitingForStart);
    assert_eq!(
        last_line(&mut white),
        "You have joined the game, waiting for all players to be ready."
    );
    white.apply_start_game(&start(0));
    assert_eq!(white.stage(), ChessStage::MyTurn);
    assert_eq!(white.view().team, Some(ChessTeam::White));
    assert_eq!(
        last_line(&mut white),
        "The game has begun, it is your turn to move."
    );

    let mut black = ChessState::default();
    black.begin(BOARD);
    black.apply_join_response(&join(BOARD, 1));
    black.apply_start_game(&start(0));
    assert_eq!(black.stage(), ChessStage::OpponentsTurn);
    assert_eq!(
        last_line(&mut black),
        "The game has begun, waiting for your opponent to move."
    );
}

#[test]
fn events_for_another_board_are_ignored() {
    let mut chess = ChessState::default();
    chess.begin(BOARD);
    lines(&mut chess);
    chess.apply_join_response(&join(OTHER_BOARD, 0));
    chess.apply_game_over(&over(OTHER_BOARD, 0));
    chess.apply_opponent_stalemate(&OpponentStalemate {
        board_guid: OTHER_BOARD,
        team: 1,
        offered: true,
    });
    assert_eq!(chess.stage(), ChessStage::Joining);
    assert!(lines(&mut chess).is_empty());
}

#[test]
fn accepted_move_is_shown_at_once_and_waits_for_the_server() {
    let mut chess = seated(ChessTeam::White);
    let result = chess.try_move(sq("e2"), sq("e4"));
    assert!(result.is_accepted());
    assert_eq!(chess.stage(), ChessStage::MoveSent);
    assert_eq!(
        chess.board().get(sq("e4")).map(|p| p.piece_type),
        Some(ChessPieceType::Pawn)
    );
    assert_eq!(last_line(&mut chess), "Move in progress.");

    chess.apply_move_response(&response(R::MOVED_TO_EMPTY_SQUARE));
    assert_eq!(chess.stage(), ChessStage::OpponentsTurn);
    assert_eq!(last_line(&mut chess), "Waiting for your opponent to move.");
}

#[test]
fn refused_move_says_why_and_keeps_the_turn() {
    let mut chess = seated(ChessTeam::White);
    assert_eq!(chess.try_move(sq("e2"), sq("e5")), R::BAD_DIRECTION);
    assert_eq!(chess.stage(), ChessStage::MyTurn);
    assert_eq!(
        last_line(&mut chess),
        "The selected piece cannot move that direction, try again.  It is your turn to move."
    );
}

#[test]
fn move_the_server_refuses_puts_the_squares_back_but_not_the_turn() {
    // The game puts back only the squares when the server refuses a move.
    // The turn has already passed on the board, so the board looks as it
    // did, but every move the player tries after it is refused as not
    // their piece.
    let mut chess = seated(ChessTeam::White);
    chess.try_move(sq("e2"), sq("e4"));
    chess.apply_move_response(&response(R::INVALID_COMMAND));
    let board = chess.board();
    assert_eq!(chess.stage(), ChessStage::MyTurn);
    assert_eq!(board.get(sq("e4")), None);
    assert_eq!(
        board.get(sq("e2")),
        Some(ChessPiece::new(ChessPieceType::Pawn, ChessTeam::White))
    );
    assert_eq!(
        last_line(&mut chess),
        "That move is invalid, try again.  It is your turn to move."
    );
    assert_eq!(chess.board().turn(), ChessTeam::Black);

    assert_eq!(chess.try_move(sq("e2"), sq("e4")), R::NOT_YOURS);
    assert_eq!(last_line(&mut chess), own_move_text(R::NOT_YOURS));
    assert_eq!(chess.stage(), ChessStage::MyTurn);
    assert_eq!(chess.board().get(sq("e4")), None);
}

#[test]
fn moves_off_turn_are_ignored() {
    let mut chess = seated(ChessTeam::Black);
    assert_eq!(chess.try_move(sq("e7"), sq("e5")), R::NONE);
    assert!(lines(&mut chess).is_empty());
}

#[test]
fn opponents_move_is_played_and_hands_over_the_turn() {
    let mut chess = seated(ChessTeam::Black);
    chess.apply_opponent_turn(&opponent_move(0, "e2", "e4"));
    assert_eq!(chess.stage(), ChessStage::MyTurn);
    assert_eq!(
        chess.board().get(sq("e4")).map(|p| p.piece_type),
        Some(ChessPieceType::Pawn)
    );
    assert_eq!(last_line(&mut chess), "It is your turn to move.");
    assert!(chess.try_move(sq("e7"), sq("e5")).is_accepted());
}

#[test]
fn opponents_move_the_rules_refuse_leaves_the_board_and_locks_the_turn() {
    // The game behaves this way too: the board stays as it was, nothing is
    // said, the turn still passes to the local player, and since the board
    // still has the opponent to move every move after it is refused as not
    // the player's piece.
    let mut chess = seated(ChessTeam::Black);
    let before = chess.board().clone();
    chess.apply_opponent_turn(&opponent_move(0, "e2", "e5"));
    assert_eq!(chess.stage(), ChessStage::MyTurn);
    assert!(lines(&mut chess).is_empty());
    assert!(chess.board().pieces().eq(before.pieces()));
    assert_eq!(chess.board().turn(), ChessTeam::White);

    assert_eq!(chess.try_move(sq("e7"), sq("e5")), R::NOT_YOURS);
    assert_eq!(last_line(&mut chess), own_move_text(R::NOT_YOURS));
    assert_eq!(chess.stage(), ChessStage::MyTurn);
}

#[test]
fn check_and_checkmate_are_announced_to_both_sides() {
    let mut white = seated(ChessTeam::White);
    white.try_move(sq("f2"), sq("f3"));
    white.apply_move_response(&response(R::MOVED_TO_EMPTY_SQUARE));
    white.apply_opponent_turn(&opponent_move(1, "e7", "e5"));
    white.try_move(sq("g2"), sq("g4"));
    white.apply_move_response(&response(R::MOVED_TO_EMPTY_SQUARE));

    white.apply_opponent_turn(&opponent_move(1, "d8", "h4"));

    assert_eq!(last_line(&mut white), "You have been checkmated!");
    assert_eq!(
        own_move_text(R::MOVED_TO_EMPTY_SQUARE | R::CHECKMATE),
        "You have checkmated your opponent!"
    );
    assert_eq!(
        own_move_text(R::CAPTURED | R::CHECK),
        "Move in progress.  Your opponent is in Check."
    );
    assert_eq!(
        opponent_move_text(R::MOVED_TO_EMPTY_SQUARE | R::CHECK),
        "You are in check!  It is your turn to move."
    );
}

#[test]
fn not_your_turn_from_the_server_has_its_own_line() {
    assert_eq!(
        own_move_text(R::NOT_YOUR_TURN),
        "Its not your turn, please wait for your opponents move."
    );
}

#[test]
fn game_over_says_how_it_ended_and_frees_the_player() {
    for (winner, expected) in [
        (
            0,
            "You are victorious!  To join a game, select and use the game board.",
        ),
        (
            1,
            "You have been defeated!  To join a game, select and use the game board.",
        ),
        (
            -1,
            "The game has ended in a stalemate.  To join a game, select and use the game board.",
        ),
        (-2, "To join a game, select and use the game board."),
    ] {
        let mut chess = seated(ChessTeam::White);
        chess.apply_game_over(&over(BOARD, winner));
        assert_eq!(last_line(&mut chess), expected);
        assert_eq!(chess.stage(), ChessStage::NotPlaying);
        assert_eq!(chess.view().team, None);
        assert!(chess.begin(BOARD));
    }
}

#[test]
fn game_over_keeps_the_final_position_and_only_a_no_result_end_closes_the_window() {
    let mut chess = seated(ChessTeam::White);
    chess.try_move(sq("e2"), sq("e4"));

    chess.apply_game_over(&over(BOARD, 1));
    assert_eq!(
        chess.board().get(sq("e4")).map(|p| p.piece_type),
        Some(ChessPieceType::Pawn)
    );
    assert_eq!(chess.view().games_ended, 0);

    chess.begin(BOARD);
    chess.apply_join_response(&join(BOARD, 0));
    assert_eq!(chess.board().get(sq("e4")), None);
    chess.apply_game_over(&over(BOARD, GameOver::NO_RESULT_WINNER));
    assert_eq!(chess.view().games_ended, 1);
}

#[test]
fn resign_ends_the_game_and_closes_the_window() {
    let mut chess = seated(ChessTeam::White);
    chess.resign();
    assert_eq!(chess.stage(), ChessStage::NotPlaying);
    assert_eq!(chess.view().games_ended, 1);
    assert_eq!(last_line(&mut chess), JOIN_PROMPT);
}

#[test]
fn resign_when_not_playing_changes_nothing() {
    let mut chess = ChessState::default();
    chess.resign();
    assert_eq!(chess.view().games_ended, 0);
    assert!(lines(&mut chess).is_empty());
}

#[test]
fn stalemate_toggles_the_offer_and_announces_the_opponents() {
    let mut chess = seated(ChessTeam::White);
    assert_eq!(chess.try_toggle_stalemate(), Some(true));
    assert!(chess.view().offering_stalemate);
    assert_eq!(chess.try_toggle_stalemate(), Some(false));

    let stalemate = |offered| OpponentStalemate {
        board_guid: BOARD,
        team: 1,
        offered,
    };
    chess.apply_opponent_stalemate(&stalemate(true));
    assert_eq!(
        last_line(&mut chess),
        "Your opponent has declared a stalemate.  To agree, press your stalemate button."
    );
    chess.apply_opponent_stalemate(&stalemate(false));
    assert_eq!(
        last_line(&mut chess),
        "Your opponent has retracted their offer of stalemate."
    );
}

#[test]
fn stalemate_and_pass_when_not_playing_say_so() {
    let mut chess = ChessState::default();
    assert_eq!(chess.try_toggle_stalemate(), None);
    assert!(!chess.try_pass());
    assert_eq!(
        lines(&mut chess),
        [
            (NOT_PLAYING_TEXT.to_owned(), LogTextType::ClientLocal),
            (NOT_PLAYING_TEXT.to_owned(), LogTextType::ClientLocal),
        ]
    );
}

#[test]
fn reset_session_ends_any_game_and_clears_the_board() {
    let mut chess = seated(ChessTeam::White);
    chess.try_move(sq("e2"), sq("e4"));
    chess.reset_session();
    assert_eq!(chess.stage(), ChessStage::NotPlaying);
    assert_eq!(chess.board().get(sq("e4")), None);
    assert!(chess.begin(BOARD));
}
