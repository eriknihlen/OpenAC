using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

public enum ContractStage : uint
{
    Available = 1,
    InProgress = 2,
    DoneOrPendingRepeat = 3,
    ProgressCounter = 4,
}

public readonly record struct ContractTracker(
    uint Version,
    uint ContractId,
    ContractStage Stage,
    double TimeWhenDone,
    double TimeWhenRepeats,
    DateTime ReceivedAt)
{
    /// <summary>The wire size of one tracker struct.</summary>
    internal const int WireSize = 4 + 4 + 4 + 8 + 8;

    public uint Progress =>
        (uint)Stage >= (uint)ContractStage.ProgressCounter
            ? (uint)Stage - (uint)ContractStage.ProgressCounter
            : 0u;

    public bool HasProgressCounter =>
        (uint)Stage >= (uint)ContractStage.ProgressCounter;
}

public readonly record struct ContractTrackerUpdate(
    ContractTracker Tracker,
    bool Delete,
    bool SetAsDisplay);

public static class ContractTrackerMessages
{
    private const int MaxTableEntries = 4096;

    public static ContractTrackerUpdate? ParseUpdate(
        ReadOnlySpan<byte> payload, DateTime receivedAt)
    {
        int pos = 0;
        try
        {
            ContractTracker tracker = ReadTracker(payload, ref pos, receivedAt);
            uint delete = ReadU32(payload, ref pos);
            uint display = ReadU32(payload, ref pos);
            return new ContractTrackerUpdate(tracker, delete != 0u, display != 0u);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<uint, ContractTracker>? ParseTable(
        ReadOnlySpan<byte> payload, DateTime receivedAt)
    {
        int pos = 0;
        try
        {
            uint header = ReadU32(payload, ref pos);
            ushort count = (ushort)(header & 0xFFFFu);
            ushort buckets = (ushort)(header >> 16);

            if (buckets == 0 && count != 0)
                throw new FormatException("invalid contract tracker table");
            if (count > MaxTableEntries)
                throw new FormatException("implausible contract tracker count");

            var result = new Dictionary<uint, ContractTracker>(count);
            for (int i = 0; i < count; i++)
            {
                uint key = ReadU32(payload, ref pos);
                result[key] = ReadTracker(payload, ref pos, receivedAt);
            }

            return result;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static ContractTracker ReadTracker(
        ReadOnlySpan<byte> source, ref int pos, DateTime receivedAt)
    {
        uint version = ReadU32(source, ref pos);
        uint contractId = ReadU32(source, ref pos);
        uint stage = ReadU32(source, ref pos);
        double whenDone = ReadDouble(source, ref pos);
        double whenRepeats = ReadDouble(source, ref pos);
        return new ContractTracker(
            version,
            contractId,
            (ContractStage)stage,
            whenDone,
            whenRepeats,
            receivedAt);
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos, 4));
        pos += 4;
        return value;
    }

    private static double ReadDouble(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 8) throw new FormatException("truncated double");
        double value = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(pos, 8));
        pos += 8;
        return value;
    }
}
