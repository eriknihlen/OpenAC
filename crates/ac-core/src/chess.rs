//! Chess: a position plus the rules for moving in it. The client checks a
//! move here first so a bad move gets an answer straight away, and shows it
//! before the server confirms; the server has the final say.
//!
//! Pawns promote to a queen only, which is all the server supports. A king
//! moving two files along its rank is a castling attempt.

use std::ops::{BitOr, BitOrAssign};

/// The board's width and height, squares.
pub const SIZE: i32 = 8;

/// The two sides. The numbers are the team values the server sends.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ChessTeam {
    /// White, team 0.
    White = 0,
    /// Black, team 1.
    Black = 1,
}

impl ChessTeam {
    /// The team of a server team value (0 or 1).
    #[must_use]
    pub fn from_wire(team: i32) -> Option<Self> {
        match team {
            0 => Some(Self::White),
            1 => Some(Self::Black),
            _ => None,
        }
    }

    /// The server's team value.
    #[must_use]
    pub fn wire(self) -> i32 {
        self as i32
    }

    /// The other side.
    #[must_use]
    pub fn opponent(self) -> Self {
        match self {
            Self::White => Self::Black,
            Self::Black => Self::White,
        }
    }
}

/// Chess piece kinds, numbered as the game numbers them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ChessPieceType {
    /// Pawn.
    Pawn = 1,
    /// Rook.
    Rook = 2,
    /// Knight.
    Knight = 3,
    /// Bishop.
    Bishop = 4,
    /// Queen.
    Queen = 5,
    /// King.
    King = 6,
}

/// A piece on the board.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ChessPiece {
    /// Its kind.
    pub piece_type: ChessPieceType,
    /// Its side.
    pub team: ChessTeam,
    /// It has moved this game (pawns' double step, castling).
    pub has_moved: bool,
}

impl ChessPiece {
    /// A piece that has not moved.
    #[must_use]
    pub fn new(piece_type: ChessPieceType, team: ChessTeam) -> Self {
        Self {
            piece_type,
            team,
            has_moved: false,
        }
    }

    /// A piece that has (or has not) moved.
    #[must_use]
    pub fn moved(piece_type: ChessPieceType, team: ChessTeam, has_moved: bool) -> Self {
        Self {
            piece_type,
            team,
            has_moved,
        }
    }
}

/// A board square. `x` is the file (0 = a) and `y` the rank (0 = White's
/// back rank), so White's pieces start on ranks 0 and 1 and Black's on 6
/// and 7.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ChessSquare {
    /// File, 0..8.
    pub x: i32,
    /// Rank, 0..8.
    pub y: i32,
}

impl ChessSquare {
    /// The square at file `x`, rank `y`.
    #[must_use]
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }

    /// Both coordinates are on the board.
    #[must_use]
    pub fn is_on_board(self) -> bool {
        (0..SIZE).contains(&self.x) && (0..SIZE).contains(&self.y)
    }
}

/// The outcome of a chess move, with the values the server uses in its
/// move responses. Values above zero are accepted moves: the low bits say
/// what kind of move it was and the high bits flag check, checkmate and
/// promotion. Zero and below are refusals. An open set: the server's
/// value is kept as sent.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct ChessMoveResult(pub i32);

impl ChessMoveResult {
    /// Nothing happened.
    pub const NONE: Self = Self(0);
    /// The piece moved to an empty square.
    pub const MOVED_TO_EMPTY_SQUARE: Self = Self(0x0001);
    /// The piece took another.
    pub const CAPTURED: Self = Self(0x0002);
    /// A pawn took another en passant.
    pub const CAPTURED_EN_PASSANT: Self = Self(0x0003);
    /// The move puts the other king in check.
    pub const CHECK: Self = Self(0x0400);
    /// The move checkmates the other king.
    pub const CHECKMATE: Self = Self(0x0800);
    /// A pawn reached the last rank.
    pub const PROMOTION: Self = Self(0x1000);

    /// The server did not understand the request.
    pub const INVALID_COMMAND: Self = Self(-1);
    /// The player is not in a game.
    pub const NOT_PLAYING: Self = Self(-2);
    /// It is the other side's turn.
    pub const NOT_YOUR_TURN: Self = Self(-3);
    /// The piece cannot move that direction.
    pub const BAD_DIRECTION: Self = Self(-100);
    /// The piece cannot move that far.
    pub const BAD_DISTANCE: Self = Self(-101);
    /// The from square is empty.
    pub const NO_PIECE: Self = Self(-102);
    /// The piece is the other side's.
    pub const NOT_YOURS: Self = Self(-103);
    /// A square is off the board.
    pub const OFF_BOARD: Self = Self(-104);
    /// The to square holds one of the side's own pieces.
    pub const WOULD_CAPTURE_OWN_PIECE: Self = Self(-105);
    /// The move would leave the side's own king in check.
    pub const WOULD_SELF_CHECK: Self = Self(-106);
    /// A square on the way is taken.
    pub const PATH_BLOCKED: Self = Self(-107);
    /// The king is in check.
    pub const CANT_CASTLE_OUT_OF_CHECK: Self = Self(-108);
    /// The king would pass through check.
    pub const CANT_CASTLE_THROUGH_CHECK: Self = Self(-109);
    /// The king or the rook has moved.
    pub const CANT_CASTLE_AFTER_MOVING: Self = Self(-110);
    /// The board is missing a king.
    pub const INVALID_BOARD_STATE: Self = Self(-111);

    const MOVE_KIND_MASK: i32 = 0x03FF;

    /// An accepted move.
    #[must_use]
    pub fn is_accepted(self) -> bool {
        self.0 > 0
    }

    /// The kind of an accepted move, without its check, checkmate or
    /// promotion flags (a refusal as it is).
    #[must_use]
    pub fn move_kind(self) -> Self {
        if self.is_accepted() {
            Self(self.0 & Self::MOVE_KIND_MASK)
        } else {
            self
        }
    }

    /// An accepted move that gives check.
    #[must_use]
    pub fn gives_check(self) -> bool {
        self.is_accepted() && self.0 & Self::CHECK.0 != 0
    }

    /// An accepted move that gives checkmate.
    #[must_use]
    pub fn gives_checkmate(self) -> bool {
        self.is_accepted() && self.0 & Self::CHECKMATE.0 != 0
    }

    /// An accepted move that promotes a pawn.
    #[must_use]
    pub fn promotes(self) -> bool {
        self.is_accepted() && self.0 & Self::PROMOTION.0 != 0
    }
}

impl BitOr for ChessMoveResult {
    type Output = Self;
    fn bitor(self, rhs: Self) -> Self {
        Self(self.0 | rhs.0)
    }
}

impl BitOrAssign for ChessMoveResult {
    fn bitor_assign(&mut self, rhs: Self) {
        self.0 |= rhs.0;
    }
}

const BACK_RANK: [ChessPieceType; 8] = [
    ChessPieceType::Rook,
    ChessPieceType::Knight,
    ChessPieceType::Bishop,
    ChessPieceType::Queen,
    ChessPieceType::King,
    ChessPieceType::Bishop,
    ChessPieceType::Knight,
    ChessPieceType::Rook,
];

const KING_STEPS: [(i32, i32); 8] = [
    (1, 0),
    (1, 1),
    (0, 1),
    (-1, 1),
    (-1, 0),
    (-1, -1),
    (0, -1),
    (1, -1),
];

/// A move worked out but not yet made.
#[derive(Debug, Clone, Copy, Default)]
struct PlannedMove {
    from: ChessSquare,
    to: ChessSquare,
    captured: Option<ChessSquare>,
    rook_from: Option<ChessSquare>,
    rook_to: Option<ChessSquare>,
}

impl PlannedMove {
    fn step(from: ChessSquare, to: ChessSquare) -> Self {
        Self {
            from,
            to,
            ..Self::default()
        }
    }
}

/// A chess position and the side to move.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChessBoard {
    squares: [Option<ChessPiece>; 64],
    /// The square a pawn skipped with its last move, which an enemy pawn
    /// may capture onto for one turn.
    en_passant_target: Option<ChessSquare>,
    en_passant_victim: ChessSquare,
    turn: ChessTeam,
}

impl Default for ChessBoard {
    fn default() -> Self {
        Self::standard()
    }
}

fn index(square: ChessSquare) -> usize {
    usize::try_from(square.y * SIZE + square.x).unwrap_or(0)
}

impl ChessBoard {
    /// The opening position, White to move.
    #[must_use]
    pub fn standard() -> Self {
        let mut board = Self::empty(ChessTeam::White);
        for (x, &back) in (0..SIZE).zip(BACK_RANK.iter()) {
            board.place(
                ChessSquare::new(x, 0),
                Some(ChessPiece::new(back, ChessTeam::White)),
            );
            board.place(
                ChessSquare::new(x, 1),
                Some(ChessPiece::new(ChessPieceType::Pawn, ChessTeam::White)),
            );
            board.place(
                ChessSquare::new(x, 6),
                Some(ChessPiece::new(ChessPieceType::Pawn, ChessTeam::Black)),
            );
            board.place(
                ChessSquare::new(x, 7),
                Some(ChessPiece::new(back, ChessTeam::Black)),
            );
        }
        board
    }

    /// An empty board, for setting up a position piece by piece.
    #[must_use]
    pub fn empty(turn: ChessTeam) -> Self {
        Self {
            squares: [None; 64],
            en_passant_target: None,
            en_passant_victim: ChessSquare::default(),
            turn,
        }
    }

    /// The side whose move it is.
    #[must_use]
    pub fn turn(&self) -> ChessTeam {
        self.turn
    }

    /// The piece on `square` (none off the board).
    #[must_use]
    pub fn get(&self, square: ChessSquare) -> Option<ChessPiece> {
        if square.is_on_board() {
            self.squares[index(square)]
        } else {
            None
        }
    }

    /// Put `piece` (or nothing) on `square`.
    ///
    /// # Panics
    ///
    /// When the square is off the board.
    pub fn place(&mut self, square: ChessSquare, piece: Option<ChessPiece>) {
        assert!(square.is_on_board(), "square {square:?} is off the board");
        self.squares[index(square)] = piece;
    }

    /// The 64 squares, rank 0 file 0 first, file by file then rank by
    /// rank.
    #[must_use]
    pub fn squares(&self) -> &[Option<ChessPiece>; 64] {
        &self.squares
    }

    /// Put every square back as it is on `snapshot` and nothing else: the
    /// side to move and the en passant chance stay as they are now.
    pub fn restore_squares(&mut self, snapshot: &ChessBoard) {
        self.squares = snapshot.squares;
    }

    /// Every piece with its square, in square order.
    pub fn pieces(&self) -> impl Iterator<Item = (ChessSquare, ChessPiece)> + '_ {
        (0..SIZE * SIZE).filter_map(|i| {
            let square = ChessSquare::new(i % SIZE, i / SIZE);
            self.get(square).map(|p| (square, p))
        })
    }

    /// `team`'s king is attacked.
    #[must_use]
    pub fn is_in_check(&self, team: ChessTeam) -> bool {
        for (square, piece) in self.pieces() {
            if piece.piece_type == ChessPieceType::King && piece.team == team {
                return self.is_attacked(square, team.opponent());
            }
        }
        false
    }

    /// Move the piece on `from` for the side to move. An accepted move is
    /// made, passes the turn, and comes back flagged with check or
    /// checkmate against the other side; a refused move leaves the board as
    /// it was.
    pub fn make_move(&mut self, from: ChessSquare, to: ChessSquare) -> ChessMoveResult {
        let (mut result, plan) = self.plan_move(from, to);
        if !result.is_accepted() {
            return result;
        }
        self.apply(&plan);
        self.turn = self.turn.opponent();
        if self.is_in_check(self.turn) {
            result |= if self.is_checkmated(self.turn) {
                ChessMoveResult::CHECKMATE
            } else {
                ChessMoveResult::CHECK
            };
        }
        result
    }

    fn plan_move(&self, from: ChessSquare, to: ChessSquare) -> (ChessMoveResult, PlannedMove) {
        let refuse = |r| (r, PlannedMove::default());
        if !from.is_on_board() || !to.is_on_board() {
            return refuse(ChessMoveResult::OFF_BOARD);
        }
        if !self.has_both_kings() {
            return refuse(ChessMoveResult::INVALID_BOARD_STATE);
        }
        let Some(piece) = self.get(from) else {
            return refuse(ChessMoveResult::NO_PIECE);
        };
        if piece.team != self.turn {
            return refuse(ChessMoveResult::NOT_YOURS);
        }
        if piece.piece_type == ChessPieceType::King && (to.x - from.x).abs() == 2 && to.y == from.y
        {
            return self.plan_castle(from, to, piece);
        }
        let (result, plan) = self.plan_reach(from, to, piece, true);
        if result.is_accepted() && !self.leaves_king_safe(&plan, piece.team) {
            return refuse(ChessMoveResult::WOULD_SELF_CHECK);
        }
        (result, plan)
    }

    /// Whether `piece` can reach `to` by its own movement, ignoring whether
    /// that would expose its king.
    fn plan_reach(
        &self,
        from: ChessSquare,
        to: ChessSquare,
        piece: ChessPiece,
        allow_en_passant: bool,
    ) -> (ChessMoveResult, PlannedMove) {
        let refuse = |r| (r, PlannedMove::default());
        if !to.is_on_board() {
            return refuse(ChessMoveResult::OFF_BOARD);
        }
        let dx = to.x - from.x;
        let forward = if piece.team == ChessTeam::White {
            to.y - from.y
        } else {
            from.y - to.y
        };
        let captured;
        let mut kind;
        if allow_en_passant
            && piece.piece_type == ChessPieceType::Pawn
            && self.en_passant_target == Some(to)
            && can_attack(piece, dx, forward)
            && self
                .get(self.en_passant_victim)
                .is_some_and(|v| v.piece_type == ChessPieceType::Pawn)
        {
            captured = Some(self.en_passant_victim);
            kind = ChessMoveResult::CAPTURED_EN_PASSANT;
        } else if let Some(target) = self.get(to) {
            if target.team == piece.team {
                return refuse(ChessMoveResult::WOULD_CAPTURE_OWN_PIECE);
            }
            if !can_attack(piece, dx, forward) {
                return refuse(ChessMoveResult::BAD_DIRECTION);
            }
            captured = Some(to);
            kind = ChessMoveResult::CAPTURED;
        } else {
            if !can_go_to(piece, dx, forward) {
                return refuse(ChessMoveResult::BAD_DIRECTION);
            }
            captured = None;
            kind = ChessMoveResult::MOVED_TO_EMPTY_SQUARE;
        }
        if !self.path_is_clear(from, to) {
            return refuse(ChessMoveResult::PATH_BLOCKED);
        }
        if piece.piece_type == ChessPieceType::Pawn && (to.y == 0 || to.y == SIZE - 1) {
            kind |= ChessMoveResult::PROMOTION;
        }
        (
            kind,
            PlannedMove {
                captured,
                ..PlannedMove::step(from, to)
            },
        )
    }

    fn plan_castle(
        &self,
        from: ChessSquare,
        to: ChessSquare,
        king: ChessPiece,
    ) -> (ChessMoveResult, PlannedMove) {
        let refuse = |r| (r, PlannedMove::default());
        if self.is_in_check(king.team) {
            return refuse(ChessMoveResult::CANT_CASTLE_OUT_OF_CHECK);
        }
        if king.has_moved {
            return refuse(ChessMoveResult::CANT_CASTLE_AFTER_MOVING);
        }
        let step = if to.x < from.x { -1 } else { 1 };
        let passing = ChessSquare::new(from.x + step, from.y);
        let mut square = passing;
        while square.is_on_board() {
            if let Some(other) = self.get(square) {
                if other.piece_type != ChessPieceType::Rook || other.team != king.team {
                    return refuse(ChessMoveResult::PATH_BLOCKED);
                }
                if other.has_moved {
                    return refuse(ChessMoveResult::CANT_CASTLE_AFTER_MOVING);
                }
                if !self.leaves_king_safe(&PlannedMove::step(from, passing), king.team) {
                    return refuse(ChessMoveResult::CANT_CASTLE_THROUGH_CHECK);
                }
                if !self.leaves_king_safe(&PlannedMove::step(from, to), king.team) {
                    return refuse(ChessMoveResult::WOULD_SELF_CHECK);
                }
                return (
                    ChessMoveResult::MOVED_TO_EMPTY_SQUARE,
                    PlannedMove {
                        rook_from: Some(square),
                        rook_to: Some(passing),
                        ..PlannedMove::step(from, to)
                    },
                );
            }
            square = ChessSquare::new(square.x + step, square.y);
        }
        refuse(ChessMoveResult::NO_PIECE)
    }

    /// Every square strictly between the two must be empty when they share
    /// a rank, file or diagonal; a knight's jump has no path.
    fn path_is_clear(&self, from: ChessSquare, to: ChessSquare) -> bool {
        let dx = to.x - from.x;
        let dy = to.y - from.y;
        let line = dx == 0 || dy == 0 || dx.abs() == dy.abs();
        if !line {
            return true;
        }
        let (sx, sy) = (dx.signum(), dy.signum());
        let mut square = ChessSquare::new(from.x + sx, from.y + sy);
        while square != to {
            if self.get(square).is_some() {
                return false;
            }
            square = ChessSquare::new(square.x + sx, square.y + sy);
        }
        true
    }

    fn is_attacked(&self, square: ChessSquare, by_team: ChessTeam) -> bool {
        self.pieces().any(|(from, piece)| {
            piece.team == by_team && self.plan_reach(from, square, piece, false).0.is_accepted()
        })
    }

    /// Whether `team`, in check, is checkmated. Two kinds of answer are
    /// looked for: the king stepping to one of the eight squares around
    /// it, or any piece moving onto the square of the piece giving check or
    /// a square between it and the king. A move that ends anywhere else
    /// does not count, so taking a checking pawn en passant, which lands
    /// behind the pawn, is not an escape: the game calls that checkmate.
    /// Castling is never tried.
    fn is_checkmated(&self, team: ChessTeam) -> bool {
        let Some((king, king_piece)) = self.king_of(team) else {
            return false;
        };
        for (dx, dy) in KING_STEPS {
            let to = ChessSquare::new(king.x + dx, king.y + dy);
            let (result, plan) = self.plan_reach(king, to, king_piece, true);
            if result.is_accepted() && self.leaves_king_safe(&plan, team) {
                return false;
            }
        }
        // Only the first piece found giving check is answered. With two
        // pieces giving check, no single capture or block can stop both,
        // so the other is never needed.
        for (from, attacker) in self.pieces() {
            if attacker.team == team
                || !self.plan_reach(from, king, attacker, false).0.is_accepted()
            {
                continue;
            }
            for square in squares_up_to_king(from, king) {
                for (defender_from, defender) in self.pieces() {
                    if defender.team != team {
                        continue;
                    }
                    let (result, plan) = self.plan_reach(defender_from, square, defender, true);
                    if result.is_accepted() && self.leaves_king_safe(&plan, team) {
                        return false;
                    }
                }
            }
            return true;
        }
        false
    }

    fn king_of(&self, team: ChessTeam) -> Option<(ChessSquare, ChessPiece)> {
        self.pieces()
            .find(|(_, p)| p.piece_type == ChessPieceType::King && p.team == team)
    }

    fn leaves_king_safe(&self, plan: &PlannedMove, team: ChessTeam) -> bool {
        let mut after = self.clone();
        after.apply(plan);
        !after.is_in_check(team)
    }

    fn apply(&mut self, plan: &PlannedMove) {
        let Some(piece) = self.get(plan.from) else {
            return;
        };
        if let Some(captured) = plan.captured {
            self.place(captured, None);
        }
        self.place(plan.from, None);
        let promotes =
            piece.piece_type == ChessPieceType::Pawn && (plan.to.y == 0 || plan.to.y == SIZE - 1);
        self.place(
            plan.to,
            Some(ChessPiece {
                piece_type: if promotes {
                    ChessPieceType::Queen
                } else {
                    piece.piece_type
                },
                has_moved: true,
                ..piece
            }),
        );
        if let (Some(rook_from), Some(rook_to)) = (plan.rook_from, plan.rook_to)
            && let Some(rook) = self.get(rook_from)
        {
            self.place(rook_from, None);
            self.place(
                rook_to,
                Some(ChessPiece {
                    has_moved: true,
                    ..rook
                }),
            );
        }
        self.en_passant_target = None;
        if piece.piece_type == ChessPieceType::Pawn && (plan.to.y - plan.from.y).abs() == 2 {
            self.en_passant_target =
                Some(ChessSquare::new(plan.from.x, (plan.from.y + plan.to.y) / 2));
            self.en_passant_victim = plan.to;
        }
    }

    fn has_both_kings(&self) -> bool {
        let mut white = false;
        let mut black = false;
        for (_, piece) in self.pieces() {
            if piece.piece_type != ChessPieceType::King {
                continue;
            }
            match piece.team {
                ChessTeam::White => white = true,
                ChessTeam::Black => black = true,
            }
        }
        white && black
    }
}

fn can_go_to(piece: ChessPiece, dx: i32, forward: i32) -> bool {
    let ax = dx.abs();
    let af = forward.abs();
    match piece.piece_type {
        ChessPieceType::Pawn => dx == 0 && (forward == 1 || (forward == 2 && !piece.has_moved)),
        ChessPieceType::Rook => (dx != 0) != (forward != 0),
        ChessPieceType::Knight => (ax == 1 && af == 2) || (ax == 2 && af == 1),
        ChessPieceType::Bishop => ax == af,
        ChessPieceType::Queen => dx == 0 || forward == 0 || ax == af,
        ChessPieceType::King => ax < 2 && af < 2,
    }
}

fn can_attack(piece: ChessPiece, dx: i32, forward: i32) -> bool {
    if piece.piece_type == ChessPieceType::Pawn {
        dx.abs() == 1 && forward == 1
    } else {
        can_go_to(piece, dx, forward)
    }
}

/// The attacker's own square, then each square along its line up to (not
/// including) the king. A knight's check has no line, so it is just the
/// knight's square.
fn squares_up_to_king(from: ChessSquare, king: ChessSquare) -> impl Iterator<Item = ChessSquare> {
    let dx = king.x - from.x;
    let dy = king.y - from.y;
    let line = dx == 0 || dy == 0 || dx.abs() == dy.abs();
    let count = if line { dx.abs().max(dy.abs()) } else { 1 };
    let (sx, sy) = (dx.signum(), dy.signum());
    (0..count).map(move |step| ChessSquare::new(from.x + sx * step, from.y + sy * step))
}

#[cfg(test)]
mod tests;
