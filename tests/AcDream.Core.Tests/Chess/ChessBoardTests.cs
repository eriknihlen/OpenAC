using AcDream.Core.Chess;

namespace AcDream.Core.Tests.Chess;

public sealed class ChessBoardTests
{
    private static ChessSquare Sq(string name) => new(name[0] - 'a', name[1] - '1');

    private static ChessPiece White(ChessPieceType type, bool moved = false)
        => new(type, ChessTeam.White, moved);

    private static ChessPiece Black(ChessPieceType type, bool moved = false)
        => new(type, ChessTeam.Black, moved);

    private static ChessMoveResult Move(ChessBoard board, string from, string to)
        => board.Move(Sq(from), Sq(to));

    /// <summary>Plays a line of moves that must all be accepted.</summary>
    private static ChessMoveResult Play(ChessBoard board, params string[] moves)
    {
        ChessMoveResult last = ChessMoveResult.None;
        foreach (string move in moves)
        {
            last = Move(board, move[..2], move[2..]);
            Assert.True(last.IsAccepted(), $"{move} was refused: {last}");
        }
        return last;
    }

    /// <summary>A board holding just the two kings plus the given pieces.</summary>
    private static ChessBoard Position(
        ChessTeam turn, string whiteKing, string blackKing,
        params (string Square, ChessPiece Piece)[] pieces)
    {
        var board = ChessBoard.CreateEmpty(turn);
        board.Place(Sq(whiteKing), White(ChessPieceType.King));
        board.Place(Sq(blackKing), Black(ChessPieceType.King));
        foreach (var (square, piece) in pieces)
            board.Place(Sq(square), piece);
        return board;
    }

    [Fact]
    public void StandardBoard_SetsUpBothArmiesWithWhiteToMove()
    {
        var board = ChessBoard.CreateStandard();

        Assert.Equal(ChessTeam.White, board.Turn);
        Assert.Equal(32, board.Pieces.Count());
        Assert.Equal(White(ChessPieceType.King), board[Sq("e1")]);
        Assert.Equal(White(ChessPieceType.Queen), board[Sq("d1")]);
        Assert.Equal(Black(ChessPieceType.King), board[Sq("e8")]);
        Assert.Equal(Black(ChessPieceType.Knight), board[Sq("g8")]);
        Assert.Equal(White(ChessPieceType.Pawn), board[Sq("a2")]);
        Assert.Equal(Black(ChessPieceType.Pawn), board[Sq("h7")]);
        Assert.Null(board[Sq("e4")]);
    }

    [Fact]
    public void AcceptedMove_MovesThePieceAndPassesTheTurn()
    {
        var board = ChessBoard.CreateStandard();

        Assert.Equal(ChessMoveResult.MovedToEmptySquare, Move(board, "e2", "e4"));

        Assert.Null(board[Sq("e2")]);
        Assert.Equal(White(ChessPieceType.Pawn, moved: true), board[Sq("e4")]);
        Assert.Equal(ChessTeam.Black, board.Turn);
    }

    [Theory]
    [InlineData("e4", "e5", ChessMoveResult.NoPiece)]
    [InlineData("e7", "e5", ChessMoveResult.NotYours)]
    [InlineData("e2", "e9", ChessMoveResult.OffBoard)]
    [InlineData("b1", "d2", ChessMoveResult.WouldCaptureOwnPiece)]
    [InlineData("e2", "e5", ChessMoveResult.BadDirection)]
    [InlineData("e2", "d3", ChessMoveResult.BadDirection)]
    [InlineData("c1", "e3", ChessMoveResult.PathBlocked)]
    [InlineData("a1", "a3", ChessMoveResult.PathBlocked)]
    public void RefusedMove_ExplainsWhyAndLeavesTheBoardAlone(string from, string to, ChessMoveResult expected)
    {
        var board = ChessBoard.CreateStandard();

        Assert.Equal(expected, Move(board, from, to));

        Assert.Equal(ChessTeam.White, board.Turn);
        Assert.Equal(32, board.Pieces.Count());
    }

    [Fact]
    public void OffBoardSquares_AreRefusedForEitherEnd()
    {
        var board = ChessBoard.CreateStandard();

        Assert.Equal(ChessMoveResult.OffBoard, board.Move(new ChessSquare(-1, 0), Sq("a3")));
        Assert.Equal(ChessMoveResult.OffBoard, board.Move(Sq("a2"), new ChessSquare(0, 8)));
    }

    [Fact]
    public void Knight_JumpsOverPieces()
    {
        var board = ChessBoard.CreateStandard();

        Assert.Equal(ChessMoveResult.MovedToEmptySquare, Move(board, "g1", "f3"));
        Assert.Equal(ChessMoveResult.MovedToEmptySquare, Move(board, "b8", "c6"));
    }

    [Fact]
    public void Pawn_StepsTwoOnlyOnItsFirstMoveAndOnlyThroughAnEmptySquare()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "e2e3", "a7a6");

        Assert.Equal(ChessMoveResult.BadDirection, Move(board, "e3", "e5"));

        Play(board, "g1f3", "a6a5");
        Assert.Equal(ChessMoveResult.PathBlocked, Move(board, "f2", "f4"));
    }

    [Fact]
    public void Pawn_CapturesDiagonallyButNotStraightAhead()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "e2e4", "d7d5");

        Assert.Equal(ChessMoveResult.Captured, Move(board, "e4", "d5"));
        Assert.Equal(White(ChessPieceType.Pawn, moved: true), board[Sq("d5")]);
        Assert.Equal(31, board.Pieces.Count());

        Play(board, "d8d6");
        Assert.Equal(ChessMoveResult.BadDirection, Move(board, "d5", "d6"));
    }

    [Fact]
    public void EnPassant_CapturesThePawnThatJustSteppedTwo()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "e2e4", "a7a6", "e4e5", "d7d5");

        Assert.Equal(ChessMoveResult.CapturedEnPassant, Move(board, "e5", "d6"));

        Assert.Null(board[Sq("d5")]);
        Assert.Equal(White(ChessPieceType.Pawn, moved: true), board[Sq("d6")]);
        Assert.Equal(31, board.Pieces.Count());
    }

    [Fact]
    public void EnPassant_IsOnlyAvailableOnTheVeryNextMove()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "e2e4", "a7a6", "e4e5", "d7d5", "h2h3", "a6a5");

        Assert.Equal(ChessMoveResult.BadDirection, Move(board, "e5", "d6"));
    }

    [Fact]
    public void PinnedPiece_CannotMoveOffThePin()
    {
        var board = Position(ChessTeam.White, "e1", "h8",
            ("e2", White(ChessPieceType.Bishop)),
            ("e8", Black(ChessPieceType.Rook)));

        Assert.Equal(ChessMoveResult.WouldSelfCheck, Move(board, "e2", "d3"));
    }

    [Fact]
    public void King_CannotStepIntoCheck()
    {
        var board = Position(ChessTeam.White, "e1", "h8", ("d8", Black(ChessPieceType.Rook)));

        Assert.Equal(ChessMoveResult.WouldSelfCheck, Move(board, "e1", "d1"));
        Assert.Equal(ChessMoveResult.MovedToEmptySquare, Move(board, "e1", "f1"));
    }

    [Fact]
    public void MoveThatAttacksTheKing_IsFlaggedCheck()
    {
        var board = Position(ChessTeam.White, "e1", "e8", ("a1", White(ChessPieceType.Rook)));

        ChessMoveResult result = Move(board, "a1", "a8");

        Assert.Equal(ChessMoveResult.MovedToEmptySquare | ChessMoveResult.Check, result);
        Assert.True(result.GivesCheck());
        Assert.False(result.GivesCheckmate());
        Assert.True(board.IsInCheck(ChessTeam.Black));
    }

    [Fact]
    public void FoolsMate_IsFlaggedCheckmate()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "f2f3", "e7e5", "g2g4");

        ChessMoveResult result = Move(board, "d8", "h4");

        Assert.Equal(ChessMoveResult.MovedToEmptySquare | ChessMoveResult.Checkmate, result);
        Assert.True(result.GivesCheckmate());
        Assert.Equal(ChessMoveResult.MovedToEmptySquare, result.MoveKind());
    }

    [Fact]
    public void BackRankMate_IsCheckmate()
    {
        var board = Position(ChessTeam.White, "a1", "g8",
            ("f7", Black(ChessPieceType.Pawn)),
            ("g7", Black(ChessPieceType.Pawn)),
            ("h7", Black(ChessPieceType.Pawn)),
            ("e1", White(ChessPieceType.Rook)));

        Assert.True(Move(board, "e1", "e8").GivesCheckmate());
    }

    [Fact]
    public void CheckThatCanBeBlocked_IsNotCheckmate()
    {
        var board = Position(ChessTeam.White, "a1", "g8",
            ("f7", Black(ChessPieceType.Pawn)),
            ("g7", Black(ChessPieceType.Pawn)),
            ("h7", Black(ChessPieceType.Pawn)),
            ("b4", Black(ChessPieceType.Bishop)),
            ("e1", White(ChessPieceType.Rook)));

        ChessMoveResult result = Move(board, "e1", "e8");

        Assert.True(result.GivesCheck());
        Assert.False(result.GivesCheckmate());
    }

    [Fact]
    public void CheckThatCanBeCaptured_IsNotCheckmate()
    {
        var board = Position(ChessTeam.White, "b1", "g8",
            ("f7", Black(ChessPieceType.Pawn)),
            ("g7", Black(ChessPieceType.Pawn)),
            ("h7", Black(ChessPieceType.Pawn)),
            ("a8", Black(ChessPieceType.Rook)),
            ("e1", White(ChessPieceType.Rook)));

        Assert.False(Move(board, "e1", "e8").GivesCheckmate());
    }

    [Fact]
    public void CheckFromAPawnThatCanOnlyBeTakenEnPassant_IsCheckmate()
    {
        // The pawn stepping to d4 checks the king on e5, whose other
        // squares are all covered. Taking it en passant would get the king
        // out of check, but that capture lands on d3, behind the pawn,
        // which is neither the checking piece's square nor between it and
        // the king, so the game does not count it and calls checkmate.
        var board = Position(ChessTeam.White, "h1", "e5",
            ("d2", White(ChessPieceType.Pawn)),
            ("e4", Black(ChessPieceType.Pawn)),
            ("d1", White(ChessPieceType.Rook)),
            ("f1", White(ChessPieceType.Rook)),
            ("a6", White(ChessPieceType.Rook)),
            ("b3", White(ChessPieceType.Bishop)));

        ChessMoveResult result = Move(board, "d2", "d4");

        Assert.Equal(ChessMoveResult.MovedToEmptySquare | ChessMoveResult.Checkmate, result);
        Assert.Equal(ChessMoveResult.CapturedEnPassant, Move(board, "e4", "d3"));
    }

    [Fact]
    public void CheckThatAnEnPassantCaptureBlocks_IsNotCheckmate()
    {
        // The bishop on b1 checks the king on h7 along c2-d3-e4-f5-g6.
        // Taking the pawn that just stepped to d4 en passant puts a pawn on
        // d3, between the bishop and the king, so it counts as an escape.
        var board = Position(ChessTeam.White, "h1", "h7",
            ("b1", White(ChessPieceType.Bishop)),
            ("d2", White(ChessPieceType.Pawn)),
            ("c4", Black(ChessPieceType.Pawn)),
            ("g1", White(ChessPieceType.Rook)),
            ("a8", White(ChessPieceType.Rook)),
            ("a6", White(ChessPieceType.Rook)));

        ChessMoveResult result = Move(board, "d2", "d4");

        Assert.Equal(ChessMoveResult.MovedToEmptySquare | ChessMoveResult.Check, result);
        Assert.Equal(ChessMoveResult.CapturedEnPassant, Move(board, "c4", "d3"));
    }

    [Fact]
    public void NoLegalMoveButNotInCheck_IsNeitherCheckNorCheckmate()
    {
        var board = Position(ChessTeam.White, "a1", "h8", ("f6", White(ChessPieceType.Queen)));

        ChessMoveResult result = Move(board, "f6", "f7");

        Assert.Equal(ChessMoveResult.MovedToEmptySquare, result);
        Assert.False(board.IsInCheck(ChessTeam.Black));
    }

    [Theory]
    [InlineData("g1", "h1", "f1")]
    [InlineData("c1", "a1", "d1")]
    public void Castling_MovesKingTwoFilesAndRookAcrossIt(string kingTo, string rookFrom, string rookTo)
    {
        var board = Position(ChessTeam.White, "e1", "e8",
            ("a1", White(ChessPieceType.Rook)),
            ("h1", White(ChessPieceType.Rook)));

        Assert.Equal(ChessMoveResult.MovedToEmptySquare, Move(board, "e1", kingTo));

        Assert.Equal(White(ChessPieceType.King, moved: true), board[Sq(kingTo)]);
        Assert.Equal(White(ChessPieceType.Rook, moved: true), board[Sq(rookTo)]);
        Assert.Null(board[Sq(rookFrom)]);
        Assert.Null(board[Sq("e1")]);
    }

    [Fact]
    public void Castling_NeedsEverySquareUpToTheRookEmpty()
    {
        var board = Position(ChessTeam.White, "e1", "e8",
            ("a1", White(ChessPieceType.Rook)),
            ("b1", White(ChessPieceType.Knight)));

        Assert.Equal(ChessMoveResult.PathBlocked, Move(board, "e1", "c1"));
    }

    [Fact]
    public void Castling_NeedsARookOnThatSide()
    {
        var board = Position(ChessTeam.White, "e1", "e8");

        Assert.Equal(ChessMoveResult.NoPiece, Move(board, "e1", "g1"));
    }

    [Fact]
    public void Castling_IsRefusedOnceTheKingOrRookHasMoved()
    {
        var kingMoved = Position(ChessTeam.White, "e1", "e8", ("h1", White(ChessPieceType.Rook)));
        kingMoved.Place(Sq("e1"), White(ChessPieceType.King, moved: true));
        var rookMoved = Position(ChessTeam.White, "e1", "e8", ("h1", White(ChessPieceType.Rook, moved: true)));

        Assert.Equal(ChessMoveResult.CantCastleAfterMoving, Move(kingMoved, "e1", "g1"));
        Assert.Equal(ChessMoveResult.CantCastleAfterMoving, Move(rookMoved, "e1", "g1"));
    }

    [Fact]
    public void Castling_IsRefusedOutOfThroughOrIntoCheck()
    {
        var outOf = Position(ChessTeam.White, "e1", "a8",
            ("h1", White(ChessPieceType.Rook)), ("e5", Black(ChessPieceType.Rook)));
        var through = Position(ChessTeam.White, "e1", "a8",
            ("h1", White(ChessPieceType.Rook)), ("f5", Black(ChessPieceType.Rook)));
        var into = Position(ChessTeam.White, "e1", "a8",
            ("h1", White(ChessPieceType.Rook)), ("g5", Black(ChessPieceType.Rook)));

        Assert.Equal(ChessMoveResult.CantCastleOutOfCheck, Move(outOf, "e1", "g1"));
        Assert.Equal(ChessMoveResult.CantCastleThroughCheck, Move(through, "e1", "g1"));
        Assert.Equal(ChessMoveResult.WouldSelfCheck, Move(into, "e1", "g1"));
    }

    [Fact]
    public void PawnReachingTheLastRank_BecomesAQueen()
    {
        var board = Position(ChessTeam.White, "a1", "h8", ("b7", White(ChessPieceType.Pawn, moved: true)));

        ChessMoveResult result = Move(board, "b7", "b8");

        Assert.True(result.Promotes());
        Assert.True(result.GivesCheck());
        Assert.Equal(ChessMoveResult.MovedToEmptySquare, result.MoveKind());
        Assert.Equal(White(ChessPieceType.Queen, moved: true), board[Sq("b8")]);
    }

    [Fact]
    public void BoardWithoutBothKings_RefusesEveryMove()
    {
        var board = ChessBoard.CreateEmpty();
        board.Place(Sq("e1"), White(ChessPieceType.King));
        board.Place(Sq("a2"), White(ChessPieceType.Pawn));

        Assert.Equal(ChessMoveResult.InvalidBoardState, Move(board, "a2", "a3"));
    }

    [Fact]
    public void Clone_IsIndependentSoAMoveCanBeUndone()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "e2e4", "d7d5");
        ChessBoard before = board.Clone();

        Play(board, "e4d5");
        board = before;

        Assert.Equal(ChessTeam.White, board.Turn);
        Assert.Equal(Black(ChessPieceType.Pawn, moved: true), board[Sq("d5")]);
        Assert.Equal(ChessMoveResult.Captured, Move(board, "e4", "d5"));
    }

    [Fact]
    public void Clone_KeepsTheEnPassantChance()
    {
        var board = ChessBoard.CreateStandard();
        Play(board, "e2e4", "a7a6", "e4e5", "d7d5");

        Assert.Equal(ChessMoveResult.CapturedEnPassant, Move(board.Clone(), "e5", "d6"));
    }
}
