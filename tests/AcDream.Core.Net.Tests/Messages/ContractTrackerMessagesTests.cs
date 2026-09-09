using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class ContractTrackerMessagesTests
{
    private static readonly DateTime Arrival = new(2026, 8, 21, 13, 5, 9, DateTimeKind.Utc);

    private static byte[] Tracker(
        uint version, uint contractId, uint stage, double whenDone, double whenRepeats)
    {
        var buffer = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), version);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), contractId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), stage);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(12), whenDone);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(20), whenRepeats);
        return buffer;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (byte[] part in parts) result.AddRange(part);
        return [.. result];
    }

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] HashHeader(ushort count, ushort buckets)
        => U32((uint)count | ((uint)buckets << 16));

    // ── 0x0315, the single update ───────────────────────────────────────

    [Fact]
    public void AnUpdateDecodesEveryFieldInOrder()
    {
        byte[] payload = Concat(
            Tracker(3u, 0x1234u, 2u, 120.5, 86400.0),
            U32(0u),      // DeleteContract
            U32(1u));     // SetAsDisplayContract

        ContractTrackerUpdate update =
            ContractTrackerMessages.ParseUpdate(payload, Arrival)!.Value;

        Assert.Equal(3u, update.Tracker.Version);
        Assert.Equal(0x1234u, update.Tracker.ContractId);
        Assert.Equal(ContractStage.InProgress, update.Tracker.Stage);
        Assert.Equal(120.5, update.Tracker.TimeWhenDone);
        Assert.Equal(86400.0, update.Tracker.TimeWhenRepeats);
        Assert.False(update.Delete);
        Assert.True(update.SetAsDisplay);
    }

    [Fact]
    public void TheTwoFlagsAreWidenedBoolsNotBytes()
    {
        byte[] payload = Concat(Tracker(1u, 7u, 1u, 0, 0), U32(1u), U32(0u));

        ContractTrackerUpdate update =
            ContractTrackerMessages.ParseUpdate(payload, Arrival)!.Value;

        Assert.True(update.Delete);
        Assert.False(update.SetAsDisplay);
    }

    [Fact]
    public void ArrivalIsStampedBecauseItIsNotOnTheWire()
    {
        ContractTrackerUpdate update = ContractTrackerMessages.ParseUpdate(
            Concat(Tracker(1u, 7u, 3u, 0, 600.0), U32(0u), U32(0u)), Arrival)!.Value;

        Assert.Equal(Arrival, update.Tracker.ReceivedAt);
    }

    [Fact]
    public void ATruncatedUpdateIsRejectedRatherThanPartiallyDecoded()
    {
        // The struct alone, with the two flags missing.
        Assert.Null(ContractTrackerMessages.ParseUpdate(Tracker(1u, 7u, 1u, 0, 0), Arrival));
        Assert.Null(ContractTrackerMessages.ParseUpdate([], Arrival));
    }


    [Theory]
    [InlineData(1u, 0u, false)]
    [InlineData(2u, 0u, false)]
    [InlineData(3u, 0u, false)]
    [InlineData(4u, 0u, true)]
    [InlineData(9u, 5u, true)]
    public void TheStageCarriesTheProgressCountAboveFour(
        uint stage, uint expectedProgress, bool expectedHasCounter)
    {
        ContractTracker tracker = ContractTrackerMessages.ParseUpdate(
            Concat(Tracker(1u, 7u, stage, 0, 0), U32(0u), U32(0u)), Arrival)!.Value.Tracker;

        Assert.Equal(expectedProgress, tracker.Progress);
        Assert.Equal(expectedHasCounter, tracker.HasProgressCounter);
    }

    // ── 0x0314, the full table ──────────────────────────────────────────

    [Fact]
    public void TheTableDecodesEveryEntryKeyedByContractId()
    {
        byte[] payload = Concat(
            HashHeader(count: 2, buckets: 8),
            U32(0x1111u), Tracker(1u, 0x1111u, 1u, 0, 0),
            U32(0x2222u), Tracker(1u, 0x2222u, 6u, 10.0, 20.0));

        IReadOnlyDictionary<uint, ContractTracker> table =
            ContractTrackerMessages.ParseTable(payload, Arrival)!;

        Assert.Equal(2, table.Count);
        Assert.Equal(ContractStage.Available, table[0x1111u].Stage);
        Assert.Equal(2u, table[0x2222u].Progress);
        Assert.Equal(Arrival, table[0x2222u].ReceivedAt);
    }

    [Fact]
    public void AnEmptyTableIsAValidAnswerNotAFailure()
    {
        IReadOnlyDictionary<uint, ContractTracker>? table =
            ContractTrackerMessages.ParseTable(HashHeader(0, 0), Arrival);

        Assert.NotNull(table);
        Assert.Empty(table!);
    }

    [Fact]
    public void ANonEmptyTableWithNoBucketsIsRejected()
    {
        Assert.Null(ContractTrackerMessages.ParseTable(
            Concat(HashHeader(count: 1, buckets: 0), U32(1u), Tracker(1u, 1u, 1u, 0, 0)),
            Arrival));
    }

    [Fact]
    public void ATruncatedTableIsRejectedRatherThanReturningThePrefix()
    {
        Assert.Null(ContractTrackerMessages.ParseTable(
            Concat(HashHeader(count: 2, buckets: 8), U32(1u), Tracker(1u, 1u, 1u, 0, 0)),
            Arrival));
    }

    [Fact]
    public void AnImplausibleCountIsRejectedWithoutAllocatingForIt()
    {
        Assert.Null(ContractTrackerMessages.ParseTable(
            HashHeader(count: 60000, buckets: 256), Arrival));
    }
}

public sealed class AbandonContractRequestTests
{
    [Fact]
    public void TheAbandonPayloadIsTheContractIdAlone()
    {
        byte[] frame = ClientCommandRequests.BuildAbandonContract(
            sequence: 7u, contractId: 0x1234u);

        // 0xF7B1 envelope, sequence, opcode, then the payload.
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(0x0316u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(16, frame.Length);
    }
}
