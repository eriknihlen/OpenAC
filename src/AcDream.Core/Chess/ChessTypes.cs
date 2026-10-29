namespace AcDream.Core.Chess;

/// <summary>The two sides. The numbers are the team values the server sends.</summary>
public enum ChessTeam
{
    White = 0,
    Black = 1,
}

/// <summary>Chess piece kinds, numbered as the game numbers them.</summary>
public enum ChessPieceType
{
    Pawn = 1,
    Rook = 2,
    Knight = 3,
    Bishop = 4,
    Queen = 5,
    King = 6,
}

public readonly record struct ChessPiece(ChessPieceType Type, ChessTeam Team, bool HasMoved = false);

/// <summary>
/// A board square. X is the file (0 = a) and Y the rank (0 = White's back
/// rank), so White's pieces start on Y 0 and 1 and Black's on 6 and 7.
/// </summary>
public readonly record struct ChessSquare(int X, int Y)
{
    public bool IsOnBoard => (uint)X < ChessBoard.Size && (uint)Y < ChessBoard.Size;
}

/// <summary>
/// The outcome of a chess move, with the same values the server uses in
/// its move responses. Values above zero are accepted moves: the low bits
/// say what kind of move it was and the high bits flag check, checkmate and
/// promotion. Zero and below are refusals.
/// </summary>
public enum ChessMoveResult
{
    None = 0,
    MovedToEmptySquare = 0x0001,
    Captured = 0x0002,
    CapturedEnPassant = 0x0003,
    Check = 0x0400,
    Checkmate = 0x0800,
    Promotion = 0x1000,

    InvalidCommand = -1,
    NotPlaying = -2,
    NotYourTurn = -3,
    BadDirection = -100,
    BadDistance = -101,
    NoPiece = -102,
    NotYours = -103,
    OffBoard = -104,
    WouldCaptureOwnPiece = -105,
    WouldSelfCheck = -106,
    PathBlocked = -107,
    CantCastleOutOfCheck = -108,
    CantCastleThroughCheck = -109,
    CantCastleAfterMoving = -110,
    InvalidBoardState = -111,
}

public static class ChessMoveResultExtensions
{
    private const int MoveKindMask = 0x03FF;

    public static bool IsAccepted(this ChessMoveResult result) => (int)result > 0;

    /// <summary>The kind of an accepted move, without its check, checkmate
    /// or promotion flags.</summary>
    public static ChessMoveResult MoveKind(this ChessMoveResult result)
        => result.IsAccepted() ? (ChessMoveResult)((int)result & MoveKindMask) : result;

    public static bool GivesCheck(this ChessMoveResult result)
        => result.IsAccepted() && (result & ChessMoveResult.Check) != 0;

    public static bool GivesCheckmate(this ChessMoveResult result)
        => result.IsAccepted() && (result & ChessMoveResult.Checkmate) != 0;

    public static bool Promotes(this ChessMoveResult result)
        => result.IsAccepted() && (result & ChessMoveResult.Promotion) != 0;
}
