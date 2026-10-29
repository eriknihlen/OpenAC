using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

/// <summary>
/// Outbound chess game actions. Chess is the only mini-game the protocol
/// carries.
/// </summary>
public static class ChessRequests
{
    public const uint GameActionEnvelope = 0xF7B1u;
    public const uint JoinOpcode = 0x0269u;
    public const uint QuitOpcode = 0x026Au;
    public const uint MoveOpcode = 0x026Bu;
    public const uint MovePassOpcode = 0x026Du;
    public const uint StalemateOpcode = 0x026Eu;

    /// <summary>
    /// 0x0269 Join: the board to sit at, then the team asked for. Always
    /// -1, which lets the server pick the side.
    /// </summary>
    public static byte[] BuildJoin(uint gameActionSequence, uint boardGuid)
    {
        byte[] body = new byte[20];
        WriteHeader(body, gameActionSequence, JoinOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), boardGuid);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), -1);
        return body;
    }

    /// <summary>0x026A Quit: resign the current game.</summary>
    public static byte[] BuildQuit(uint gameActionSequence)
        => BuildEmpty(gameActionSequence, QuitOpcode);

    /// <summary>0x026B Move: from square, then to square (board x, y).</summary>
    public static byte[] BuildMove(
        uint gameActionSequence, int fromX, int fromY, int toX, int toY)
    {
        byte[] body = new byte[28];
        WriteHeader(body, gameActionSequence, MoveOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), fromX);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), fromY);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), toX);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), toY);
        return body;
    }

    /// <summary>0x026D MovePass: give up this turn.</summary>
    public static byte[] BuildMovePass(uint gameActionSequence)
        => BuildEmpty(gameActionSequence, MovePassOpcode);

    /// <summary>0x026E Stalemate: offer (true) or retract (false) a draw.</summary>
    public static byte[] BuildStalemate(uint gameActionSequence, bool offer)
    {
        byte[] body = new byte[16];
        WriteHeader(body, gameActionSequence, StalemateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), offer ? 1u : 0u);
        return body;
    }

    private static byte[] BuildEmpty(uint gameActionSequence, uint opcode)
    {
        byte[] body = new byte[12];
        WriteHeader(body, gameActionSequence, opcode);
        return body;
    }

    private static void WriteHeader(byte[] body, uint sequence, uint opcode)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), opcode);
    }
}
