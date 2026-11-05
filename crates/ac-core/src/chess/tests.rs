//! Cases ported from OpenAC `tests/AcDream.Core.Tests/Chess/ChessBoardTests.cs`.

use super::*;

type R = ChessMoveResult;
use ChessPieceType as P;

fn sq(name: &str) -> ChessSquare {
    let b = name.as_bytes();
    ChessSquare::new(
        i32::from(b[0]) - i32::from(b'a'),
        i32::from(b[1]) - i32::from(b'1'),
    )
}

fn white(t: ChessPieceType) -> ChessPiece {
    ChessPiece::new(t, ChessTeam::White)
}

fn black(t: ChessPieceType) -> ChessPiece {
    ChessPiece::new(t, ChessTeam::Black)
}

fn moved(mut p: ChessPiece) -> ChessPiece {
    p.has_moved = true;
    p
}

fn mv(board: &mut ChessBoard, from: &str, to: &str) -> ChessMoveResult {
    board.make_move(sq(from), sq(to))
}

/// Plays a line of moves that must all be accepted.
fn play(board: &mut ChessBoard, moves: &[&str]) -> ChessMoveResult {
    let mut last = R::NONE;
    for m in moves {
        last = mv(board, &m[..2], &m[2..]);
        assert!(last.is_accepted(), "{m} was refused: {last:?}");
    }
    last
}

/// A board holding just the two kings plus the given pieces.
fn position(
    turn: ChessTeam,
    white_king: &str,
    black_king: &str,
    pieces: &[(&str, ChessPiece)],
) -> ChessBoard {
    let mut board = ChessBoard::empty(turn);
    board.place(sq(white_king), Some(white(P::King)));
    board.place(sq(black_king), Some(black(P::King)));
    for (square, piece) in pieces {
        board.place(sq(square), Some(*piece));
    }
    board
}

#[test]
fn standard_board_sets_up_both_armies_with_white_to_move() {
    let board = ChessBoard::standard();
    assert_eq!(board.turn(), ChessTeam::White);
    assert_eq!(board.pieces().count(), 32);
    assert_eq!(board.get(sq("e1")), Some(white(P::King)));
    assert_eq!(board.get(sq("d1")), Some(white(P::Queen)));
    assert_eq!(board.get(sq("e8")), Some(black(P::King)));
    assert_eq!(board.get(sq("g8")), Some(black(P::Knight)));
    assert_eq!(board.get(sq("a2")), Some(white(P::Pawn)));
    assert_eq!(board.get(sq("h7")), Some(black(P::Pawn)));
    assert_eq!(board.get(sq("e4")), None);
}

#[test]
fn accepted_move_moves_the_piece_and_passes_the_turn() {
    let mut board = ChessBoard::standard();
    assert_eq!(mv(&mut board, "e2", "e4"), R::MOVED_TO_EMPTY_SQUARE);
    assert_eq!(board.get(sq("e2")), None);
    assert_eq!(board.get(sq("e4")), Some(moved(white(P::Pawn))));
    assert_eq!(board.turn(), ChessTeam::Black);
}

#[test]
fn refused_move_explains_why_and_leaves_the_board_alone() {
    for (from, to, expected) in [
        ("e4", "e5", R::NO_PIECE),
        ("e7", "e5", R::NOT_YOURS),
        ("e2", "e9", R::OFF_BOARD),
        ("b1", "d2", R::WOULD_CAPTURE_OWN_PIECE),
        ("e2", "e5", R::BAD_DIRECTION),
        ("e2", "d3", R::BAD_DIRECTION),
        ("c1", "e3", R::PATH_BLOCKED),
        ("a1", "a3", R::PATH_BLOCKED),
    ] {
        let mut board = ChessBoard::standard();
        assert_eq!(mv(&mut board, from, to), expected, "{from}{to}");
        assert_eq!(board.turn(), ChessTeam::White);
        assert_eq!(board.pieces().count(), 32);
    }
}

#[test]
fn off_board_squares_are_refused_for_either_end() {
    let mut board = ChessBoard::standard();
    assert_eq!(
        board.make_move(ChessSquare::new(-1, 0), sq("a3")),
        R::OFF_BOARD
    );
    assert_eq!(
        board.make_move(sq("a2"), ChessSquare::new(0, 8)),
        R::OFF_BOARD
    );
}

#[test]
fn knight_jumps_over_pieces() {
    let mut board = ChessBoard::standard();
    assert_eq!(mv(&mut board, "g1", "f3"), R::MOVED_TO_EMPTY_SQUARE);
    assert_eq!(mv(&mut board, "b8", "c6"), R::MOVED_TO_EMPTY_SQUARE);
}

#[test]
fn pawn_steps_two_only_on_its_first_move_and_only_through_an_empty_square() {
    let mut board = ChessBoard::standard();
    play(&mut board, &["e2e3", "a7a6"]);
    assert_eq!(mv(&mut board, "e3", "e5"), R::BAD_DIRECTION);
    play(&mut board, &["g1f3", "a6a5"]);
    assert_eq!(mv(&mut board, "f2", "f4"), R::PATH_BLOCKED);
}

#[test]
fn pawn_captures_diagonally_but_not_straight_ahead() {
    let mut board = ChessBoard::standard();
    play(&mut board, &["e2e4", "d7d5"]);
    assert_eq!(mv(&mut board, "e4", "d5"), R::CAPTURED);
    assert_eq!(board.get(sq("d5")), Some(moved(white(P::Pawn))));
    assert_eq!(board.pieces().count(), 31);
    play(&mut board, &["d8d6"]);
    assert_eq!(mv(&mut board, "d5", "d6"), R::BAD_DIRECTION);
}

#[test]
fn en_passant_captures_the_pawn_that_just_stepped_two() {
    let mut board = ChessBoard::standard();
    play(&mut board, &["e2e4", "a7a6", "e4e5", "d7d5"]);
    assert_eq!(mv(&mut board, "e5", "d6"), R::CAPTURED_EN_PASSANT);
    assert_eq!(board.get(sq("d5")), None);
    assert_eq!(board.get(sq("d6")), Some(moved(white(P::Pawn))));
    assert_eq!(board.pieces().count(), 31);
}

#[test]
fn en_passant_is_only_available_on_the_very_next_move() {
    let mut board = ChessBoard::standard();
    play(
        &mut board,
        &["e2e4", "a7a6", "e4e5", "d7d5", "h2h3", "a6a5"],
    );
    assert_eq!(mv(&mut board, "e5", "d6"), R::BAD_DIRECTION);
}

#[test]
fn pinned_piece_cannot_move_off_the_pin() {
    let mut board = position(
        ChessTeam::White,
        "e1",
        "h8",
        &[("e2", white(P::Bishop)), ("e8", black(P::Rook))],
    );
    assert_eq!(mv(&mut board, "e2", "d3"), R::WOULD_SELF_CHECK);
}

#[test]
fn king_cannot_step_into_check() {
    let mut board = position(ChessTeam::White, "e1", "h8", &[("d8", black(P::Rook))]);
    assert_eq!(mv(&mut board, "e1", "d1"), R::WOULD_SELF_CHECK);
    assert_eq!(mv(&mut board, "e1", "f1"), R::MOVED_TO_EMPTY_SQUARE);
}

#[test]
fn move_that_attacks_the_king_is_flagged_check() {
    let mut board = position(ChessTeam::White, "e1", "e8", &[("a1", white(P::Rook))]);
    let result = mv(&mut board, "a1", "a8");
    assert_eq!(result, R::MOVED_TO_EMPTY_SQUARE | R::CHECK);
    assert!(result.gives_check());
    assert!(!result.gives_checkmate());
    assert!(board.is_in_check(ChessTeam::Black));
}

#[test]
fn fools_mate_is_flagged_checkmate() {
    let mut board = ChessBoard::standard();
    play(&mut board, &["f2f3", "e7e5", "g2g4"]);
    let result = mv(&mut board, "d8", "h4");
    assert_eq!(result, R::MOVED_TO_EMPTY_SQUARE | R::CHECKMATE);
    assert!(result.gives_checkmate());
    assert_eq!(result.move_kind(), R::MOVED_TO_EMPTY_SQUARE);
}

fn back_rank(extra: &[(&str, ChessPiece)], white_king: &str) -> ChessBoard {
    let mut pieces = vec![
        ("f7", black(P::Pawn)),
        ("g7", black(P::Pawn)),
        ("h7", black(P::Pawn)),
        ("e1", white(P::Rook)),
    ];
    pieces.extend_from_slice(extra);
    position(ChessTeam::White, white_king, "g8", &pieces)
}

#[test]
fn back_rank_mate_is_checkmate() {
    let mut board = back_rank(&[], "a1");
    assert!(mv(&mut board, "e1", "e8").gives_checkmate());
}

#[test]
fn check_that_can_be_blocked_is_not_checkmate() {
    let mut board = back_rank(&[("b4", black(P::Bishop))], "a1");
    let result = mv(&mut board, "e1", "e8");
    assert!(result.gives_check());
    assert!(!result.gives_checkmate());
}

#[test]
fn check_that_can_be_captured_is_not_checkmate() {
    let mut board = back_rank(&[("a8", black(P::Rook))], "b1");
    assert!(!mv(&mut board, "e1", "e8").gives_checkmate());
}

#[test]
fn check_from_a_pawn_that_can_only_be_taken_en_passant_is_checkmate() {
    // The pawn stepping to d4 checks the king on e5, whose other squares
    // are all covered. Taking it en passant would get the king out of
    // check, but that capture lands on d3, behind the pawn, which is
    // neither the checking piece's square nor between it and the king, so
    // the game does not count it and calls checkmate.
    let mut board = position(
        ChessTeam::White,
        "h1",
        "e5",
        &[
            ("d2", white(P::Pawn)),
            ("e4", black(P::Pawn)),
            ("d1", white(P::Rook)),
            ("f1", white(P::Rook)),
            ("a6", white(P::Rook)),
            ("b3", white(P::Bishop)),
        ],
    );
    let result = mv(&mut board, "d2", "d4");
    assert_eq!(result, R::MOVED_TO_EMPTY_SQUARE | R::CHECKMATE);
    assert_eq!(mv(&mut board, "e4", "d3"), R::CAPTURED_EN_PASSANT);
}

#[test]
fn check_that_an_en_passant_capture_blocks_is_not_checkmate() {
    // The bishop on b1 checks the king on h7 along c2-d3-e4-f5-g6. Taking
    // the pawn that just stepped to d4 en passant puts a pawn on d3,
    // between the bishop and the king, so it counts as an escape.
    let mut board = position(
        ChessTeam::White,
        "h1",
        "h7",
        &[
            ("b1", white(P::Bishop)),
            ("d2", white(P::Pawn)),
            ("c4", black(P::Pawn)),
            ("g1", white(P::Rook)),
            ("a8", white(P::Rook)),
            ("a6", white(P::Rook)),
        ],
    );
    let result = mv(&mut board, "d2", "d4");
    assert_eq!(result, R::MOVED_TO_EMPTY_SQUARE | R::CHECK);
    assert_eq!(mv(&mut board, "c4", "d3"), R::CAPTURED_EN_PASSANT);
}

#[test]
fn no_legal_move_but_not_in_check_is_neither_check_nor_checkmate() {
    let mut board = position(ChessTeam::White, "a1", "h8", &[("f6", white(P::Queen))]);
    let result = mv(&mut board, "f6", "f7");
    assert_eq!(result, R::MOVED_TO_EMPTY_SQUARE);
    assert!(!board.is_in_check(ChessTeam::Black));
}

#[test]
fn castling_moves_king_two_files_and_rook_across_it() {
    for (king_to, rook_from, rook_to) in [("g1", "h1", "f1"), ("c1", "a1", "d1")] {
        let mut board = position(
            ChessTeam::White,
            "e1",
            "e8",
            &[("a1", white(P::Rook)), ("h1", white(P::Rook))],
        );
        assert_eq!(mv(&mut board, "e1", king_to), R::MOVED_TO_EMPTY_SQUARE);
        assert_eq!(board.get(sq(king_to)), Some(moved(white(P::King))));
        assert_eq!(board.get(sq(rook_to)), Some(moved(white(P::Rook))));
        assert_eq!(board.get(sq(rook_from)), None);
        assert_eq!(board.get(sq("e1")), None);
    }
}

#[test]
fn castling_needs_every_square_up_to_the_rook_empty() {
    let mut board = position(
        ChessTeam::White,
        "e1",
        "e8",
        &[("a1", white(P::Rook)), ("b1", white(P::Knight))],
    );
    assert_eq!(mv(&mut board, "e1", "c1"), R::PATH_BLOCKED);
}

#[test]
fn castling_needs_a_rook_on_that_side() {
    let mut board = position(ChessTeam::White, "e1", "e8", &[]);
    assert_eq!(mv(&mut board, "e1", "g1"), R::NO_PIECE);
}

#[test]
fn castling_is_refused_once_the_king_or_rook_has_moved() {
    let mut king_moved = position(ChessTeam::White, "e1", "e8", &[("h1", white(P::Rook))]);
    king_moved.place(sq("e1"), Some(moved(white(P::King))));
    let mut rook_moved = position(
        ChessTeam::White,
        "e1",
        "e8",
        &[("h1", moved(white(P::Rook)))],
    );
    assert_eq!(mv(&mut king_moved, "e1", "g1"), R::CANT_CASTLE_AFTER_MOVING);
    assert_eq!(mv(&mut rook_moved, "e1", "g1"), R::CANT_CASTLE_AFTER_MOVING);
}

#[test]
fn castling_is_refused_out_of_through_or_into_check() {
    let case = |rook: &str| {
        position(
            ChessTeam::White,
            "e1",
            "a8",
            &[("h1", white(P::Rook)), (rook, black(P::Rook))],
        )
    };
    assert_eq!(mv(&mut case("e5"), "e1", "g1"), R::CANT_CASTLE_OUT_OF_CHECK);
    assert_eq!(
        mv(&mut case("f5"), "e1", "g1"),
        R::CANT_CASTLE_THROUGH_CHECK
    );
    assert_eq!(mv(&mut case("g5"), "e1", "g1"), R::WOULD_SELF_CHECK);
}

#[test]
fn pawn_reaching_the_last_rank_becomes_a_queen() {
    let mut board = position(
        ChessTeam::White,
        "a1",
        "h8",
        &[("b7", moved(white(P::Pawn)))],
    );
    let result = mv(&mut board, "b7", "b8");
    assert!(result.promotes());
    assert!(result.gives_check());
    assert_eq!(result.move_kind(), R::MOVED_TO_EMPTY_SQUARE);
    assert_eq!(board.get(sq("b8")), Some(moved(white(P::Queen))));
}

#[test]
fn board_without_both_kings_refuses_every_move() {
    let mut board = ChessBoard::empty(ChessTeam::White);
    board.place(sq("e1"), Some(white(P::King)));
    board.place(sq("a2"), Some(white(P::Pawn)));
    assert_eq!(mv(&mut board, "a2", "a3"), R::INVALID_BOARD_STATE);
}

#[test]
fn clone_is_independent_so_a_move_can_be_undone() {
    let mut board = ChessBoard::standard();
    play(&mut board, &["e2e4", "d7d5"]);
    let before = board.clone();
    play(&mut board, &["e4d5"]);
    let mut board = before;
    assert_eq!(board.turn(), ChessTeam::White);
    assert_eq!(board.get(sq("d5")), Some(moved(black(P::Pawn))));
    assert_eq!(mv(&mut board, "e4", "d5"), R::CAPTURED);
}

#[test]
fn clone_keeps_the_en_passant_chance() {
    let mut board = ChessBoard::standard();
    play(&mut board, &["e2e4", "a7a6", "e4e5", "d7d5"]);
    assert_eq!(mv(&mut board.clone(), "e5", "d6"), R::CAPTURED_EN_PASSANT);
}

#[test]
fn restore_squares_puts_back_only_the_pieces() {
    // The squares come back; the turn and the en passant chance stay as
    // they are (what a server-refused move restores).
    let mut board = ChessBoard::standard();
    let before = board.clone();
    play(&mut board, &["e2e4"]);
    board.restore_squares(&before);
    assert_eq!(board.get(sq("e2")), Some(white(P::Pawn)));
    assert_eq!(board.get(sq("e4")), None);
    assert_eq!(board.turn(), ChessTeam::Black);
}
