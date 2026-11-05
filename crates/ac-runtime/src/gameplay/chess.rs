//! The local player's chess game.
//!
//! Using a board in the world begins it (a join goes out); the board
//! window's clicks and buttons are commands that ask it whether to send a
//! move, a pass, a draw offer or a resignation; the server's game events
//! move it along. Every step says a line the way the game always has.
//!
//! A move shown before the server confirms it has its squares put back if
//! the server refuses it; the turn stays passed. When a game ends the final
//! position stays on the board until the next game;
//! [`ChessView::games_ended`] counts the games that ended by resigning or
//! with no result, which close the window.

use ac_core::chat::LogTextType;
use ac_core::chess::{ChessBoard, ChessMoveResult, ChessSquare, ChessTeam};
use ac_proto::chess::{
    ChessMoveType, GameOver, JoinGameResponse, MoveResponse, OpponentStalemate, OpponentTurn,
    StartGame,
};

/// What a game over or a resignation says, after how it ended.
pub const JOIN_PROMPT: &str = "To join a game, select and use the game board.";
/// What a pass or a draw offer says outside a game.
pub const NOT_PLAYING_TEXT: &str = "You are not currently playing a game.";

/// Where the local player is in a chess game.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ChessStage {
    /// No game.
    #[default]
    NotPlaying,
    /// A join was sent for a board; no answer yet.
    Joining,
    /// Seated, waiting for the other side to sit down.
    WaitingForStart,
    /// The local player's move.
    MyTurn,
    /// A move was shown and sent; waiting for the server to accept or
    /// refuse it.
    MoveSent,
    /// The other side's move.
    OpponentsTurn,
}

/// A chess request (the board window's clicks and buttons).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ChessCommand {
    /// Move the piece on `from` to `to` on the local player's turn: shown
    /// at once and sent when the board's rules accept it.
    Move {
        /// From square.
        from: ChessSquare,
        /// To square.
        to: ChessSquare,
    },
    /// Give up the turn.
    Pass,
    /// Offer a draw, or take the offer back.
    ToggleStalemate,
    /// The player confirmed resigning: the quit goes out either way, and a
    /// game in progress ends.
    Resign,
}

/// The game as a window shows it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChessView {
    /// Counts changes.
    pub revision: u64,
    /// Where the player is.
    pub stage: ChessStage,
    /// The board played at (0: none).
    pub board_guid: u32,
    /// The side the player sits on.
    pub team: Option<ChessTeam>,
    /// The player offers a draw.
    pub offering_stalemate: bool,
    /// Games that ended by resigning or with no result.
    pub games_ended: u64,
    /// The position the board shows.
    pub board: ChessBoard,
}

impl Default for ChessView {
    fn default() -> Self {
        ChessState::default().view()
    }
}

/// See the module documentation.
#[derive(Debug, Clone, Default)]
pub struct ChessState {
    board: ChessBoard,
    before_move: Option<ChessBoard>,
    stage: ChessStage,
    board_guid: u32,
    team: Option<ChessTeam>,
    offering_stalemate: bool,
    games_ended: u64,
    revision: u64,
    lines: Vec<(String, LogTextType)>,
}

impl ChessState {
    /// The game as it stands.
    #[must_use]
    pub fn view(&self) -> ChessView {
        ChessView {
            revision: self.revision,
            stage: self.stage,
            board_guid: self.board_guid,
            team: self.team,
            offering_stalemate: self.offering_stalemate,
            games_ended: self.games_ended,
            board: self.board.clone(),
        }
    }

    /// Counts changes.
    #[must_use]
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Where the player is.
    #[must_use]
    pub fn stage(&self) -> ChessStage {
        self.stage
    }

    /// The board played at (0: none).
    #[must_use]
    pub fn board_guid(&self) -> u32 {
        self.board_guid
    }

    /// The position the board shows.
    #[must_use]
    pub fn board(&self) -> &ChessBoard {
        &self.board
    }

    /// Take the lines said, oldest first, with their text class.
    pub fn drain_lines(&mut self) -> std::vec::Drain<'_, (String, LogTextType)> {
        self.lines.drain(..)
    }

    fn say(&mut self, text: impl Into<String>, class: LogTextType) {
        self.lines.push((text.into(), class));
    }

    /// The local player used a board. True means send a join for it.
    pub fn begin(&mut self, board_guid: u32) -> bool {
        if self.board_guid == board_guid {
            self.say(
                "You are already playing this game.",
                LogTextType::ClientLocal,
            );
            return false;
        }
        if self.board_guid != 0 {
            self.say(
                "You are already playing another game.",
                LogTextType::ClientLocal,
            );
            return false;
        }
        self.board_guid = board_guid;
        self.stage = ChessStage::Joining;
        self.revision += 1;
        self.say(
            "Attempting to join game, please wait...",
            LogTextType::Default,
        );
        true
    }

    /// Try a move on the local player's turn. An accepted move is shown at
    /// once and should be sent; anything else leaves the board alone. Off
    /// turn the board does not move and nothing is said.
    pub fn try_move(&mut self, from: ChessSquare, to: ChessSquare) -> ChessMoveResult {
        if self.stage != ChessStage::MyTurn {
            return ChessMoveResult::NONE;
        }
        let before = self.board.clone();
        let result = self.board.make_move(from, to);
        if result.is_accepted() {
            self.before_move = Some(before);
            self.stage = ChessStage::MoveSent;
            self.revision += 1;
        }
        self.say(own_move_text(result), LogTextType::Default);
        result
    }

    /// True means send a pass.
    pub fn try_pass(&mut self) -> bool {
        if self.board_guid != 0 {
            return true;
        }
        self.say(NOT_PLAYING_TEXT, LogTextType::ClientLocal);
        false
    }

    /// Offer a draw, or take the offer back: the offer to send, or `None`
    /// outside a game.
    pub fn try_toggle_stalemate(&mut self) -> Option<bool> {
        if self.board_guid != 0 {
            self.offering_stalemate = !self.offering_stalemate;
            self.revision += 1;
            return Some(self.offering_stalemate);
        }
        self.say(NOT_PLAYING_TEXT, LogTextType::ClientLocal);
        None
    }

    /// The local player confirmed resigning. The caller sends the quit
    /// either way; a game in progress also ends here.
    pub fn resign(&mut self) {
        if self.board_guid == 0 {
            return;
        }
        self.end_game();
        self.games_ended += 1;
        self.say(JOIN_PROMPT, LogTextType::Default);
    }

    /// The server's answer to a join.
    pub fn apply_join_response(&mut self, response: &JoinGameResponse) {
        if response.board_guid != self.board_guid || self.stage != ChessStage::Joining {
            return;
        }
        let text = if let Some(team) = ChessTeam::from_wire(response.team) {
            self.team = Some(team);
            self.stage = ChessStage::WaitingForStart;
            self.board = ChessBoard::standard();
            self.before_move = None;
            self.offering_stalemate = false;
            "You have joined the game, waiting for all players to be ready."
        } else {
            self.board_guid = 0;
            self.stage = ChessStage::NotPlaying;
            "You could not join that game."
        };
        self.revision += 1;
        self.say(text, LogTextType::Default);
    }

    /// Both sides are seated.
    pub fn apply_start_game(&mut self, start: &StartGame) {
        if start.board_guid != self.board_guid || self.stage != ChessStage::WaitingForStart {
            return;
        }
        let text = if self.team.map(ChessTeam::wire) == Some(start.first_team) {
            self.stage = ChessStage::MyTurn;
            "The game has begun, it is your turn to move."
        } else {
            self.stage = ChessStage::OpponentsTurn;
            "The game has begun, waiting for your opponent to move."
        };
        self.revision += 1;
        self.say(text, LogTextType::Default);
    }

    /// The server's answer to the move the local player sent.
    pub fn apply_move_response(&mut self, response: &MoveResponse) {
        if response.board_guid != self.board_guid || self.stage != ChessStage::MoveSent {
            return;
        }
        let result = ChessMoveResult(response.result);
        let text = if result.is_accepted() {
            self.stage = ChessStage::OpponentsTurn;
            "Waiting for your opponent to move.".to_owned()
        } else {
            // The board is left as the game leaves it: the squares go back
            // to how they were before the move, but the turn has already
            // passed on the board and stays passed. The board looks
            // unchanged, and every move the player tries after this is
            // refused as not their piece until the game ends.
            if let Some(before) = self.before_move.take() {
                self.board.restore_squares(&before);
            }
            self.stage = ChessStage::MyTurn;
            own_move_text(result)
        };
        self.before_move = None;
        self.revision += 1;
        self.say(text, LogTextType::Default);
    }

    /// The other side's move.
    pub fn apply_opponent_turn(&mut self, turn: &OpponentTurn) {
        if turn.board_guid != self.board_guid || self.stage != ChessStage::OpponentsTurn {
            return;
        }
        let mut text = None;
        let m = &turn.chess_move;
        if m.move_type == ChessMoveType::FROM_TO {
            let result = self.board.make_move(
                ChessSquare::new(m.from_x, m.from_y),
                ChessSquare::new(m.to_x, m.to_y),
            );
            if result.is_accepted() {
                text = Some(opponent_move_text(result));
            }
        }
        // The turn passes even when the board's rules refuse a move the
        // server accepted, and nothing is said. That is how the game
        // behaves: the board keeps the opponent to move, so every move the
        // player then tries is refused as not theirs until the game ends.
        // Matching it keeps both clients in the same state.
        self.stage = ChessStage::MyTurn;
        self.revision += 1;
        if let Some(text) = text {
            self.say(text, LogTextType::Default);
        }
    }

    /// The other side offered or withdrew a draw.
    pub fn apply_opponent_stalemate(&mut self, stalemate: &OpponentStalemate) {
        if stalemate.board_guid != self.board_guid {
            return;
        }
        self.say(
            if stalemate.offered {
                "Your opponent has declared a stalemate.  To agree, press your stalemate button."
            } else {
                "Your opponent has retracted their offer of stalemate."
            },
            LogTextType::Default,
        );
    }

    /// The game ended.
    pub fn apply_game_over(&mut self, over: &GameOver) {
        if over.board_guid != self.board_guid {
            return;
        }
        let result = match over.winner {
            GameOver::NO_RESULT_WINNER => "",
            GameOver::STALEMATE_WINNER => "The game has ended in a stalemate.  ",
            w if Some(w) == self.team.map(ChessTeam::wire) => "You are victorious!  ",
            _ => "You have been defeated!  ",
        };
        if over.winner == GameOver::NO_RESULT_WINNER {
            self.games_ended += 1;
        }
        self.end_game();
        self.say(format!("{result}{JOIN_PROMPT}"), LogTextType::Default);
    }

    /// The character left the world: any game ends and the board is set
    /// up again.
    pub fn reset_session(&mut self) {
        self.end_game();
        self.board = ChessBoard::standard();
    }

    fn end_game(&mut self) {
        self.board_guid = 0;
        self.stage = ChessStage::NotPlaying;
        self.team = None;
        self.offering_stalemate = false;
        self.before_move = None;
        self.revision += 1;
    }
}

/// The line for the local player's own move: the server's verdict or the
/// board's.
#[must_use]
pub fn own_move_text(result: ChessMoveResult) -> String {
    if result.gives_checkmate() {
        return "You have checkmated your opponent!".to_owned();
    }
    if result.is_accepted() {
        return if result.gives_check() {
            "Move in progress.  Your opponent is in Check."
        } else {
            "Move in progress."
        }
        .to_owned();
    }
    if result == ChessMoveResult::NOT_YOUR_TURN {
        return "Its not your turn, please wait for your opponents move.".to_owned();
    }
    type R = ChessMoveResult;
    let reason = match result {
        R::CANT_CASTLE_AFTER_MOVING => "You cannot castle after moving the King or Rook",
        R::CANT_CASTLE_THROUGH_CHECK => "You cannot castle through check",
        R::CANT_CASTLE_OUT_OF_CHECK => "You cannot castle out of check",
        R::PATH_BLOCKED => "You can only move through empty squares",
        R::WOULD_SELF_CHECK => "That move would put you in check",
        R::WOULD_CAPTURE_OWN_PIECE => "You cannot attack your own pieces",
        R::OFF_BOARD => "You cannot move off the board",
        R::NOT_YOURS => "The selected piece is not yours",
        R::NO_PIECE => "You tried to move an empty square",
        R::BAD_DISTANCE => "The selected piece cannot move that far",
        R::BAD_DIRECTION => "The selected piece cannot move that direction",
        _ => "That move is invalid",
    };
    format!("{reason}, try again.  It is your turn to move.")
}

/// The line for the other side's move.
#[must_use]
pub fn opponent_move_text(result: ChessMoveResult) -> String {
    if result.gives_checkmate() {
        return "You have been checkmated!".to_owned();
    }
    if result.gives_check() {
        "You are in check!  It is your turn to move."
    } else {
        "It is your turn to move."
    }
    .to_owned()
}

#[cfg(test)]
#[path = "chess_tests.rs"]
mod tests;
