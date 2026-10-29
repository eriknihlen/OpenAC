namespace AcDream.Core.Net.Messages;

/// <summary>
/// The kind of move carried in a chess move record; it decides which
/// fields follow on the wire.
/// </summary>
public enum ChessMoveType : uint
{
    Invalid = 0,
    Pass = 1,
    Resign = 2,
    Stalemate = 3,
    /// <summary>One square: x, y.</summary>
    Grid = 4,
    /// <summary>From square then to square: x, y, x, y.</summary>
    FromTo = 5,
    /// <summary>The guid of the piece moved.</summary>
    SelectedPiece = 6,
}
