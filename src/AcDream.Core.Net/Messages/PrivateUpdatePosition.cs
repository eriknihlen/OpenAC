using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

/// <summary>
/// One saved position slot changed on the player — the slot the server rewrites
/// when the character dies, recalls, or ties a portal. Without it the client
/// keeps whatever the login description carried and answers questions like
/// "where is my corpse?" with a stale landmark.
/// </summary>
public static class PrivateUpdatePosition
{
    public const uint Opcode = 0x02DBu;

    // opcode(4) + sequence(1) + position type(4) + cell(4) + origin(12) + orientation(16)
    public const int BodySize = 41;

    public readonly record struct Parsed(
        byte Sequence,
        uint PositionType,
        PlayerDescriptionParser.WorldPosition Position);

    /// <summary>Parse a raw 0x02DB body. Returns null on opcode mismatch / truncation.</summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < BodySize) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != Opcode) return null;

        byte sequence = body[4];                    // quality sequence (not honored)
        uint positionType = BinaryPrimitives.ReadUInt32LittleEndian(body[5..]);

        int pos = 9;
        uint cell = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        float x = ReadF32(body, ref pos);
        float y = ReadF32(body, ref pos);
        float z = ReadF32(body, ref pos);
        float qw = ReadF32(body, ref pos);
        float qx = ReadF32(body, ref pos);
        float qy = ReadF32(body, ref pos);
        float qz = ReadF32(body, ref pos);

        return new Parsed(
            sequence,
            positionType,
            new PlayerDescriptionParser.WorldPosition(cell, x, y, z, qw, qx, qy, qz));
    }

    private static float ReadF32(ReadOnlySpan<byte> src, ref int pos)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(src[pos..]);
        pos += 4;
        return value;
    }
}
