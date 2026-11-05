//! Chess, the one game played on a game board: the requests and the events.
//!
//! Every event leads with the board's guid; a team is the side, 0 (White)
//! or 1 (Black). Game-action builders take the session's next action
//! sequence number and are sent on the UI queue.

use ac_bits::Reader;

use crate::wire::{action, bare_action, read_bool32};
use crate::{GameActionType, Result};

/// The kind of move a move record carries; it decides which fields follow
/// on the wire. An open set: any `u32` the server sends is kept.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ChessMoveType(pub u32);

impl ChessMoveType {
    /// No move.
    pub const INVALID: Self = Self(0);
    /// The turn was passed.
    pub const PASS: Self = Self(1);
    /// The side resigned.
    pub const RESIGN: Self = Self(2);
    /// A draw.
    pub const STALEMATE: Self = Self(3);
    /// One square: x, y.
    pub const GRID: Self = Self(4);
    /// From square then to square: x, y, x, y.
    pub const FROM_TO: Self = Self(5);
    /// The guid of the piece moved.
    pub const SELECTED_PIECE: Self = Self(6);
}

/// The answer to a join ([`crate::GameEventType::JOIN_GAME_RESPONSE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct JoinGameResponse {
    /// The board.
    pub board_guid: u32,
    /// The team joined as, or -1 when the join failed.
    pub team: i32,
}

impl JoinGameResponse {
    /// Parse the event payload: board, team.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            board_guid: r.u32()?,
            team: r.i32()?,
        })
    }
}

/// Both sides are seated ([`crate::GameEventType::START_GAME`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StartGame {
    /// The board.
    pub board_guid: u32,
    /// The team that moves first.
    pub first_team: i32,
}

impl StartGame {
    /// Parse the event payload: board, first team.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            board_guid: r.u32()?,
            first_team: r.i32()?,
        })
    }
}

/// The server's verdict on the player's last move
/// ([`crate::GameEventType::MOVE_RESPONSE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MoveResponse {
    /// The board.
    pub board_guid: u32,
    /// The move result code: above zero accepted, else refused.
    pub result: i32,
}

impl MoveResponse {
    /// Parse the event payload: board, result.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            board_guid: r.u32()?,
            result: r.i32()?,
        })
    }
}

/// One move record. Which fields are set depends on the type: a
/// [`ChessMoveType::GRID`] square lands in `from_x`/`from_y`,
/// [`ChessMoveType::FROM_TO`] fills all four coordinates and
/// [`ChessMoveType::SELECTED_PIECE`] fills only `piece_guid`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ChessMove {
    /// The kind of move.
    pub move_type: ChessMoveType,
    /// Who moved.
    pub player_guid: u32,
    /// From file (or the grid square's).
    pub from_x: i32,
    /// From rank (or the grid square's).
    pub from_y: i32,
    /// To file.
    pub to_x: i32,
    /// To rank.
    pub to_y: i32,
    /// The piece moved.
    pub piece_guid: u32,
}

/// The move the other side just made
/// ([`crate::GameEventType::OPPONENT_TURN`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OpponentTurn {
    /// The board.
    pub board_guid: u32,
    /// The team that moved.
    pub team: i32,
    /// The move: type, player, then the type's fields.
    pub chess_move: ChessMove,
}

impl OpponentTurn {
    /// Parse the event payload: board, team, then the move record (type,
    /// player, then the fields only the grid, from-to and selected-piece
    /// types carry).
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        let board_guid = r.u32()?;
        let team = r.i32()?;
        let move_type = ChessMoveType(r.u32()?);
        let player_guid = r.u32()?;
        let mut chess_move = ChessMove {
            move_type,
            player_guid,
            ..ChessMove::default()
        };
        match move_type {
            ChessMoveType::GRID => {
                chess_move.from_x = r.i32()?;
                chess_move.from_y = r.i32()?;
            }
            ChessMoveType::FROM_TO => {
                chess_move.from_x = r.i32()?;
                chess_move.from_y = r.i32()?;
                chess_move.to_x = r.i32()?;
                chess_move.to_y = r.i32()?;
            }
            ChessMoveType::SELECTED_PIECE => chess_move.piece_guid = r.u32()?,
            _ => {}
        }
        Ok(Self {
            board_guid,
            team,
            chess_move,
        })
    }
}

/// The other side offered or withdrew a draw
/// ([`crate::GameEventType::OPPONENT_STALEMATE`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OpponentStalemate {
    /// The board.
    pub board_guid: u32,
    /// The team.
    pub team: i32,
    /// Offered (true) or withdrawn.
    pub offered: bool,
}

impl OpponentStalemate {
    /// Parse the event payload: board, team, offered.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            board_guid: r.u32()?,
            team: r.i32()?,
            offered: read_bool32(&mut r)?,
        })
    }
}

/// The game ended ([`crate::GameEventType::GAME_OVER`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct GameOver {
    /// The board.
    pub board_guid: u32,
    /// The winning team, [`GameOver::STALEMATE_WINNER`] or
    /// [`GameOver::NO_RESULT_WINNER`].
    pub winner: i32,
}

impl GameOver {
    /// The winner of a game that ended in a draw.
    pub const STALEMATE_WINNER: i32 = -1;
    /// The winner of a game that ended with no result (nobody won or lost).
    pub const NO_RESULT_WINNER: i32 = -2;

    /// Parse the event payload: board, winner.
    pub fn parse(payload: &[u8]) -> Result<Self> {
        let mut r = Reader::new(payload);
        Ok(Self {
            board_guid: r.u32()?,
            winner: r.i32()?,
        })
    }
}

/// Sit at a board: the board, then the team asked for, always -1, which
/// lets the server pick the side.
#[must_use]
pub fn build_join(sequence: u32, board_guid: u32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::GAME_JOIN);
    w.u32(board_guid);
    w.i32(-1);
    w.into_vec()
}

/// Resign the current game.
#[must_use]
pub fn build_quit(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::GAME_QUIT)
}

/// Move: from square, then to square (board x, y).
#[must_use]
pub fn build_move(sequence: u32, from_x: i32, from_y: i32, to_x: i32, to_y: i32) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::GAME_MOVE);
    w.i32(from_x);
    w.i32(from_y);
    w.i32(to_x);
    w.i32(to_y);
    w.into_vec()
}

/// Give up this turn.
#[must_use]
pub fn build_move_pass(sequence: u32) -> Vec<u8> {
    bare_action(sequence, GameActionType::GAME_MOVE_PASS)
}

/// Offer (true) or withdraw (false) a draw.
#[must_use]
pub fn build_stalemate(sequence: u32, offer: bool) -> Vec<u8> {
    let mut w = action(sequence, GameActionType::GAME_STALEMATE);
    w.u32(u32::from(offer));
    w.into_vec()
}

#[cfg(test)]
mod tests {
    //! Cases ported from OpenAC
    //! `tests/AcDream.Core.Net.Tests/Messages/ChessMessagesTests.cs`.

    use super::*;
    use crate::test_util::{assert_every_prefix_fails, words};

    const BOARD: u32 = 0x7A9B_4001;

    fn ints(values: &[i32]) -> Vec<u32> {
        values.iter().map(|v| v.cast_unsigned()).collect()
    }

    #[test]
    fn build_join_writes_board_and_asks_for_any_team() {
        let body = build_join(7, BOARD);
        assert_eq!(body.len(), 20);
        assert_eq!(body, words(&[0xF7B1, 7, 0x0269, BOARD, u32::MAX]));
    }

    #[test]
    fn empty_bodied_actions_write_envelope_sequence_opcode_only() {
        assert_eq!(build_quit(3), words(&[0xF7B1, 3, 0x026A]));
        assert_eq!(build_move_pass(3), words(&[0xF7B1, 3, 0x026D]));
    }

    #[test]
    fn build_move_writes_from_then_to_squares() {
        let body = build_move(9, 4, 1, 4, 3);
        assert_eq!(body.len(), 28);
        assert_eq!(body, words(&[0xF7B1, 9, 0x026B, 4, 1, 4, 3]));
    }

    #[test]
    fn build_stalemate_writes_offer_as_one_or_zero() {
        for (offer, wire) in [(true, 1), (false, 0)] {
            let body = build_stalemate(2, offer);
            assert_eq!(body.len(), 16);
            assert_eq!(body, words(&[0xF7B1, 2, 0x026E, wire]));
        }
    }

    #[test]
    fn parse_join_game_response_reads_board_and_team_including_refusal() {
        let joined = words(&[BOARD, 1]);
        let refused = words(&ints(&[BOARD.cast_signed(), -1]));
        assert_eq!(
            JoinGameResponse::parse(&joined),
            Ok(JoinGameResponse {
                board_guid: BOARD,
                team: 1
            })
        );
        assert_eq!(JoinGameResponse::parse(&refused).unwrap().team, -1);
        assert!(JoinGameResponse::parse(&joined[..4]).is_err());
        assert_every_prefix_fails(&joined, JoinGameResponse::parse);
    }

    #[test]
    fn parse_start_game_and_move_response_read_board_and_value() {
        let start = words(&[BOARD, 0]);
        let refused = words(&ints(&[BOARD.cast_signed(), -106]));
        assert_eq!(
            StartGame::parse(&start),
            Ok(StartGame {
                board_guid: BOARD,
                first_team: 0
            })
        );
        assert_eq!(
            MoveResponse::parse(&refused),
            Ok(MoveResponse {
                board_guid: BOARD,
                result: -106
            })
        );
        assert!(StartGame::parse(&start[..7]).is_err());
        assert!(MoveResponse::parse(&refused[..7]).is_err());
    }

    #[test]
    fn parse_opponent_turn_reads_a_from_to_move() {
        // The opponent-turn event: board, team, then the move (type,
        // player, from x/y, to x/y).
        let wire = words(&[BOARD, 1, 5, 0x5000_0002, 6, 7, 5, 5]);
        let parsed = OpponentTurn::parse(&wire).unwrap();
        assert_eq!(parsed.board_guid, BOARD);
        assert_eq!(parsed.team, 1);
        assert_eq!(
            parsed.chess_move,
            ChessMove {
                move_type: ChessMoveType::FROM_TO,
                player_guid: 0x5000_0002,
                from_x: 6,
                from_y: 7,
                to_x: 5,
                to_y: 5,
                piece_guid: 0
            }
        );
        assert_every_prefix_fails(&wire, OpponentTurn::parse);
    }

    #[test]
    fn parse_opponent_turn_reads_grid_and_selected_piece_moves() {
        let grid = words(&[BOARD, 0, 4, 0x5000_0002, 3, 4]);
        let piece = words(&[BOARD, 0, 6, 0x5000_0002, 0x8000_0123]);
        let grid_move = OpponentTurn::parse(&grid).unwrap().chess_move;
        let piece_move = OpponentTurn::parse(&piece).unwrap().chess_move;
        assert_eq!((grid_move.from_x, grid_move.from_y), (3, 4));
        assert_eq!(piece_move.piece_guid, 0x8000_0123);
    }

    #[test]
    fn parse_opponent_turn_accepts_a_bare_move_and_rejects_a_truncated_one() {
        let pass = words(&[BOARD, 0, 1, 0x5000_0002]);
        let from_to = words(&[BOARD, 0, 5, 0x5000_0002, 6, 7, 5, 5]);
        assert_eq!(
            OpponentTurn::parse(&pass).unwrap().chess_move.move_type,
            ChessMoveType::PASS
        );
        assert!(OpponentTurn::parse(&from_to[..from_to.len() - 4]).is_err());
        assert!(OpponentTurn::parse(&pass[..12]).is_err());
    }

    #[test]
    fn parse_opponent_stalemate_reads_offer_and_retraction() {
        let offer = words(&[BOARD, 1, 1]);
        let retract = words(&[BOARD, 1, 0]);
        assert_eq!(
            OpponentStalemate::parse(&offer),
            Ok(OpponentStalemate {
                board_guid: BOARD,
                team: 1,
                offered: true
            })
        );
        assert!(!OpponentStalemate::parse(&retract).unwrap().offered);
        assert!(OpponentStalemate::parse(&offer[..8]).is_err());
    }

    #[test]
    fn parse_game_over_reads_board_and_winner() {
        for winner in [0, 1, GameOver::STALEMATE_WINNER, GameOver::NO_RESULT_WINNER] {
            let wire = words(&ints(&[BOARD.cast_signed(), winner]));
            assert_eq!(
                GameOver::parse(&wire),
                Ok(GameOver {
                    board_guid: BOARD,
                    winner
                })
            );
            assert!(GameOver::parse(&wire[..4]).is_err());
        }
    }
}
