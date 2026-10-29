using System;
using System.Collections.Generic;

namespace AcDream.Core.Chess;

/// <summary>
/// A chess position plus the rules for moving in it. The client checks a
/// move here first so a bad move gets an answer straight away, and shows
/// it before the server confirms; the server has the final say.
/// </summary>
/// <remarks>
/// Pawns promote to a queen only, which is all the server supports. A
/// king moving two files along its rank is a castling attempt.
/// </remarks>
public sealed class ChessBoard
{
    public const int Size = 8;

    private static readonly ChessPieceType[] BackRank =
    {
        ChessPieceType.Rook, ChessPieceType.Knight, ChessPieceType.Bishop, ChessPieceType.Queen,
        ChessPieceType.King, ChessPieceType.Bishop, ChessPieceType.Knight, ChessPieceType.Rook,
    };

    private static readonly (int Dx, int Dy)[] KingSteps =
    {
        (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
    };

    private readonly ChessPiece?[] _squares = new ChessPiece?[Size * Size];

    /// <summary>The square a pawn skipped with its last move, which an
    /// enemy pawn may capture onto for one turn.</summary>
    private ChessSquare? _enPassantTarget;
    private ChessSquare _enPassantVictim;

    private ChessBoard(ChessTeam turn) => Turn = turn;

    /// <summary>The side whose move it is.</summary>
    public ChessTeam Turn { get; private set; }

    public static ChessBoard CreateStandard()
    {
        var board = new ChessBoard(ChessTeam.White);
        for (int x = 0; x < Size; x++)
        {
            board.Place(new ChessSquare(x, 0), new ChessPiece(BackRank[x], ChessTeam.White));
            board.Place(new ChessSquare(x, 1), new ChessPiece(ChessPieceType.Pawn, ChessTeam.White));
            board.Place(new ChessSquare(x, 6), new ChessPiece(ChessPieceType.Pawn, ChessTeam.Black));
            board.Place(new ChessSquare(x, 7), new ChessPiece(BackRank[x], ChessTeam.Black));
        }
        return board;
    }

    /// <summary>An empty board, for setting up a position piece by piece.</summary>
    public static ChessBoard CreateEmpty(ChessTeam turn = ChessTeam.White) => new(turn);

    public ChessPiece? this[ChessSquare square]
        => square.IsOnBoard ? _squares[Index(square)] : null;

    public void Place(ChessSquare square, ChessPiece? piece)
    {
        if (!square.IsOnBoard)
            throw new ArgumentOutOfRangeException(nameof(square), square, "Square is off the board.");
        _squares[Index(square)] = piece;
    }

    public IEnumerable<(ChessSquare Square, ChessPiece Piece)> Pieces
    {
        get
        {
            for (int i = 0; i < _squares.Length; i++)
            {
                if (_squares[i] is ChessPiece piece)
                    yield return (new ChessSquare(i % Size, i / Size), piece);
            }
        }
    }

    public ChessBoard Clone()
    {
        var copy = new ChessBoard(Turn)
        {
            _enPassantTarget = _enPassantTarget,
            _enPassantVictim = _enPassantVictim,
        };
        Array.Copy(_squares, copy._squares, _squares.Length);
        return copy;
    }

    /// <summary>
    /// Puts every square back as it is on <paramref name="snapshot"/> and
    /// nothing else: the side to move and the en passant chance stay as
    /// they are now.
    /// </summary>
    public void RestoreSquares(ChessBoard snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Array.Copy(snapshot._squares, _squares, _squares.Length);
    }

    public bool IsInCheck(ChessTeam team)
    {
        foreach (var (square, piece) in Pieces)
        {
            if (piece.Type == ChessPieceType.King && piece.Team == team)
                return IsAttacked(square, Opponent(team));
        }
        return false;
    }

    /// <summary>
    /// Moves the piece on <paramref name="from"/> for the side to move. An
    /// accepted move is applied, passes the turn, and comes back flagged
    /// with check or checkmate against the other side; a refused move
    /// leaves the board as it was.
    /// </summary>
    public ChessMoveResult Move(ChessSquare from, ChessSquare to)
    {
        var (result, plan) = PlanMove(from, to);
        if (!result.IsAccepted())
            return result;

        Apply(plan);
        Turn = Opponent(Turn);
        if (IsInCheck(Turn))
            result |= IsCheckmated(Turn) ? ChessMoveResult.Checkmate : ChessMoveResult.Check;
        return result;
    }

    private (ChessMoveResult, PlannedMove) PlanMove(ChessSquare from, ChessSquare to)
    {
        if (!from.IsOnBoard || !to.IsOnBoard)
            return (ChessMoveResult.OffBoard, default);
        if (!HasBothKings())
            return (ChessMoveResult.InvalidBoardState, default);
        if (this[from] is not ChessPiece piece)
            return (ChessMoveResult.NoPiece, default);
        if (piece.Team != Turn)
            return (ChessMoveResult.NotYours, default);

        if (piece.Type == ChessPieceType.King && Math.Abs(to.X - from.X) == 2 && to.Y == from.Y)
            return PlanCastle(from, to, piece);

        var (result, plan) = PlanReach(from, to, piece, allowEnPassant: true);
        if (result.IsAccepted() && !LeavesKingSafe(plan, piece.Team))
            return (ChessMoveResult.WouldSelfCheck, default);
        return (result, plan);
    }

    /// <summary>
    /// Whether <paramref name="piece"/> can reach <paramref name="to"/> by
    /// its own movement, ignoring whether that would expose its king.
    /// </summary>
    private (ChessMoveResult, PlannedMove) PlanReach(
        ChessSquare from, ChessSquare to, ChessPiece piece, bool allowEnPassant)
    {
        if (!to.IsOnBoard)
            return (ChessMoveResult.OffBoard, default);

        int dx = to.X - from.X;
        int forward = piece.Team == ChessTeam.White ? to.Y - from.Y : from.Y - to.Y;
        ChessSquare? captured;
        ChessMoveResult kind;

        if (allowEnPassant
            && piece.Type == ChessPieceType.Pawn
            && _enPassantTarget == to
            && CanAttack(piece, dx, forward)
            && this[_enPassantVictim] is { Type: ChessPieceType.Pawn })
        {
            captured = _enPassantVictim;
            kind = ChessMoveResult.CapturedEnPassant;
        }
        else if (this[to] is ChessPiece target)
        {
            if (target.Team == piece.Team)
                return (ChessMoveResult.WouldCaptureOwnPiece, default);
            if (!CanAttack(piece, dx, forward))
                return (ChessMoveResult.BadDirection, default);
            captured = to;
            kind = ChessMoveResult.Captured;
        }
        else
        {
            if (!CanGoTo(piece, dx, forward))
                return (ChessMoveResult.BadDirection, default);
            captured = null;
            kind = ChessMoveResult.MovedToEmptySquare;
        }

        if (!PathIsClear(from, to))
            return (ChessMoveResult.PathBlocked, default);

        if (piece.Type == ChessPieceType.Pawn && (to.Y == 0 || to.Y == Size - 1))
            kind |= ChessMoveResult.Promotion;
        return (kind, new PlannedMove(from, to, captured, null, null));
    }

    private (ChessMoveResult, PlannedMove) PlanCastle(ChessSquare from, ChessSquare to, ChessPiece king)
    {
        if (IsInCheck(king.Team))
            return (ChessMoveResult.CantCastleOutOfCheck, default);
        if (king.HasMoved)
            return (ChessMoveResult.CantCastleAfterMoving, default);

        int step = to.X < from.X ? -1 : 1;
        var passing = new ChessSquare(from.X + step, from.Y);
        for (var square = passing; square.IsOnBoard; square = new ChessSquare(square.X + step, square.Y))
        {
            if (this[square] is not ChessPiece other)
                continue;
            if (other.Type != ChessPieceType.Rook || other.Team != king.Team)
                return (ChessMoveResult.PathBlocked, default);
            if (other.HasMoved)
                return (ChessMoveResult.CantCastleAfterMoving, default);
            if (!LeavesKingSafe(new PlannedMove(from, passing, null, null, null), king.Team))
                return (ChessMoveResult.CantCastleThroughCheck, default);
            if (!LeavesKingSafe(new PlannedMove(from, to, null, null, null), king.Team))
                return (ChessMoveResult.WouldSelfCheck, default);
            return (ChessMoveResult.MovedToEmptySquare,
                new PlannedMove(from, to, null, square, passing));
        }
        return (ChessMoveResult.NoPiece, default);
    }

    private static bool CanGoTo(ChessPiece piece, int dx, int forward)
    {
        int ax = Math.Abs(dx);
        int af = Math.Abs(forward);
        return piece.Type switch
        {
            ChessPieceType.Pawn => dx == 0 && (forward == 1 || (forward == 2 && !piece.HasMoved)),
            ChessPieceType.Rook => (dx != 0) != (forward != 0),
            ChessPieceType.Knight => (ax == 1 && af == 2) || (ax == 2 && af == 1),
            ChessPieceType.Bishop => ax == af,
            ChessPieceType.Queen => dx == 0 || forward == 0 || ax == af,
            ChessPieceType.King => ax < 2 && af < 2,
            _ => false,
        };
    }

    private static bool CanAttack(ChessPiece piece, int dx, int forward)
        => piece.Type == ChessPieceType.Pawn
            ? Math.Abs(dx) == 1 && forward == 1
            : CanGoTo(piece, dx, forward);

    /// <summary>Every square strictly between the two must be empty when
    /// they share a rank, file or diagonal; a knight's jump has no path.</summary>
    private bool PathIsClear(ChessSquare from, ChessSquare to)
    {
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        bool line = dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy);
        if (!line)
            return true;

        int sx = Math.Sign(dx);
        int sy = Math.Sign(dy);
        for (var square = new ChessSquare(from.X + sx, from.Y + sy);
             square != to;
             square = new ChessSquare(square.X + sx, square.Y + sy))
        {
            if (this[square] is not null)
                return false;
        }
        return true;
    }

    private bool IsAttacked(ChessSquare square, ChessTeam byTeam)
    {
        foreach (var (from, piece) in Pieces)
        {
            if (piece.Team == byTeam
                && PlanReach(from, square, piece, allowEnPassant: false).Item1.IsAccepted())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="team"/>, in check, is checkmated. Two kinds
    /// of answer are looked for: the king stepping to one of the eight
    /// squares around it, or any piece moving onto the square of the piece
    /// giving check or a square between it and the king. A move that ends
    /// anywhere else does not count, so taking a checking pawn en passant,
    /// which lands behind the pawn, is not an escape: the game calls that
    /// checkmate. Castling is never tried.
    /// </summary>
    private bool IsCheckmated(ChessTeam team)
    {
        if (KingOf(team) is not (ChessSquare king, ChessPiece kingPiece))
            return false;

        foreach (var (dx, dy) in KingSteps)
        {
            var to = new ChessSquare(king.X + dx, king.Y + dy);
            var (result, plan) = PlanReach(king, to, kingPiece, allowEnPassant: true);
            if (result.IsAccepted() && LeavesKingSafe(plan, team))
                return false;
        }

        // Only the first piece found giving check is answered. With two
        // pieces giving check, no single capture or block can stop both,
        // so the other is never needed.
        foreach (var (from, attacker) in Pieces)
        {
            if (attacker.Team == team
                || !PlanReach(from, king, attacker, allowEnPassant: false).Item1.IsAccepted())
                continue;

            foreach (ChessSquare square in SquaresUpToKing(from, king))
            {
                foreach (var (defenderFrom, defender) in Pieces)
                {
                    if (defender.Team != team)
                        continue;
                    var (result, plan) = PlanReach(defenderFrom, square, defender, allowEnPassant: true);
                    if (result.IsAccepted() && LeavesKingSafe(plan, team))
                        return false;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>The attacker's own square, then each square along its line
    /// up to (not including) the king. A knight's check has no line, so it
    /// is just the knight's square.</summary>
    private static IEnumerable<ChessSquare> SquaresUpToKing(ChessSquare from, ChessSquare king)
    {
        int dx = king.X - from.X;
        int dy = king.Y - from.Y;
        bool line = dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy);
        int count = line ? Math.Max(Math.Abs(dx), Math.Abs(dy)) : 1;
        int sx = Math.Sign(dx);
        int sy = Math.Sign(dy);
        for (int step = 0; step < count; step++)
            yield return new ChessSquare(from.X + sx * step, from.Y + sy * step);
    }

    private (ChessSquare, ChessPiece)? KingOf(ChessTeam team)
    {
        foreach (var (square, piece) in Pieces)
        {
            if (piece.Type == ChessPieceType.King && piece.Team == team)
                return (square, piece);
        }
        return null;
    }

    private bool LeavesKingSafe(PlannedMove plan, ChessTeam team)
    {
        ChessBoard after = Clone();
        after.Apply(plan);
        return !after.IsInCheck(team);
    }

    private void Apply(PlannedMove plan)
    {
        ChessPiece piece = this[plan.From]!.Value;
        if (plan.Captured is ChessSquare captured)
            Place(captured, null);
        Place(plan.From, null);

        bool promotes = piece.Type == ChessPieceType.Pawn && (plan.To.Y == 0 || plan.To.Y == Size - 1);
        Place(plan.To, piece with
        {
            Type = promotes ? ChessPieceType.Queen : piece.Type,
            HasMoved = true,
        });

        if (plan.RookFrom is ChessSquare rookFrom && plan.RookTo is ChessSquare rookTo)
        {
            ChessPiece rook = this[rookFrom]!.Value;
            Place(rookFrom, null);
            Place(rookTo, rook with { HasMoved = true });
        }

        _enPassantTarget = null;
        if (piece.Type == ChessPieceType.Pawn && Math.Abs(plan.To.Y - plan.From.Y) == 2)
        {
            _enPassantTarget = new ChessSquare(plan.From.X, (plan.From.Y + plan.To.Y) / 2);
            _enPassantVictim = plan.To;
        }
    }

    private bool HasBothKings()
    {
        bool white = false, black = false;
        foreach (var (_, piece) in Pieces)
        {
            if (piece.Type != ChessPieceType.King) continue;
            if (piece.Team == ChessTeam.White) white = true;
            else black = true;
        }
        return white && black;
    }

    private static ChessTeam Opponent(ChessTeam team)
        => team == ChessTeam.White ? ChessTeam.Black : ChessTeam.White;

    private static int Index(ChessSquare square) => square.Y * Size + square.X;

    private readonly record struct PlannedMove(
        ChessSquare From,
        ChessSquare To,
        ChessSquare? Captured,
        ChessSquare? RookFrom,
        ChessSquare? RookTo);
}
