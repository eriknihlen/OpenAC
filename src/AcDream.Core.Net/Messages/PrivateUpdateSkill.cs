using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PrivateUpdateSkill
{
    public const uint Opcode = 0x02DDu;

    /// <summary>Parsed skill update. Ranks widened from the wire's u16.</summary>
    public readonly record struct Parsed(
        byte Sequence,
        uint SkillId,
        uint Ranks,
        ushort AdjustPP,
        uint AdvancementClass,
        uint Xp,
        uint Init,
        uint Resistance,
        double LastUsed);

    /// <summary>
    /// Parse a raw <c>PrivateUpdateSkill (0x02DD)</c> body. Returns
    /// <c>null</c> on opcode mismatch or truncation.
    /// </summary>
    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        // 4 (opcode) + 1 (seq) + 4 + 2 + 2 + 4*4 + 8 = 37 bytes minimum.
        if (body.Length < 37) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != Opcode) return null;

        int pos = 4;
        byte seq        = body[pos]; pos += 1;
        uint skillId    = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        ushort ranks    = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]); pos += 2;
        ushort adjustPP = BinaryPrimitives.ReadUInt16LittleEndian(body[pos..]); pos += 2;
        uint sac        = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint xp         = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint init       = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        uint resistance = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]); pos += 4;
        double lastUsed = BitConverter.Int64BitsToDouble(
            BinaryPrimitives.ReadInt64LittleEndian(body[pos..]));
        return new Parsed(
            seq, skillId, ranks, adjustPP, sac, xp, init, resistance, lastUsed);
    }
}
