using System;
using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class SetTurbineChatChannels
{
    /// <summary>GameEvent sub-opcode (NOT a top-level GameMessage opcode).</summary>
    public const uint EventType = 0x0295u;

    /// <summary>Payload size in bytes (10 u32 fields).</summary>
    public const int PayloadSize = 40;

    public readonly record struct Parsed(
        uint AllegianceRoom,
        uint GeneralRoom,
        uint TradeRoom,
        uint LfgRoom,
        uint RoleplayRoom,
        uint OlthoiRoom,
        uint SocietyRoom,
        uint SocietyCelestialHandRoom,
        uint SocietyEldrytchWebRoom,
        uint SocietyRadiantBloodRoom);

    public static Parsed? TryParse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PayloadSize) return null;

        uint allegiance       = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        uint general          = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4,  4));
        uint trade            = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8,  4));
        uint lfg              = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        uint roleplay         = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
        uint olthoi           = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(20, 4));
        uint society          = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(24, 4));
        uint societyCelHan    = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(28, 4));
        uint societyEldWeb    = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(32, 4));
        uint societyRadBlo    = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(36, 4));

        return new Parsed(
            AllegianceRoom: allegiance,
            GeneralRoom: general,
            TradeRoom: trade,
            LfgRoom: lfg,
            RoleplayRoom: roleplay,
            OlthoiRoom: olthoi,
            SocietyRoom: society,
            SocietyCelestialHandRoom: societyCelHan,
            SocietyEldrytchWebRoom: societyEldWeb,
            SocietyRadiantBloodRoom: societyRadBlo);
    }

    public static byte[] Serialize(Parsed parsed)
    {
        var buf = new byte[PayloadSize];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span,            parsed.AllegianceRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4,  4), parsed.GeneralRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8,  4), parsed.TradeRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), parsed.LfgRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), parsed.RoleplayRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), parsed.OlthoiRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), parsed.SocietyRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28, 4), parsed.SocietyCelestialHandRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(32, 4), parsed.SocietyEldrytchWebRoom);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(36, 4), parsed.SocietyRadiantBloodRoom);
        return buf;
    }
}
