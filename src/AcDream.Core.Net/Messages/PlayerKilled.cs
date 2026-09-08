using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class PlayerKilled
{
    public const uint Opcode = 0x019Eu;

    public readonly record struct Parsed(
        string DeathMessage,
        uint VictimGuid,
        uint KillerGuid);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) return null;
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (opcode != Opcode) return null;

        try
        {
            int pos = 4;
            string deathMessage = StringReader.ReadString16L(body, ref pos);
            if (body.Length - pos < 8) return null;
            uint victimGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
            uint killerGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            return new Parsed(deathMessage, victimGuid, killerGuid);
        }
        catch { return null; }
    }
}
