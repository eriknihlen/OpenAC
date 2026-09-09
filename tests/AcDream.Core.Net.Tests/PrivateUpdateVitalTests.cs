using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class PrivateUpdateVitalTests
{
    private static byte[] BuildFull(byte seq, uint vital, uint ranks, uint start, uint xp, uint current)
    {
        // u32 opcode (0x02E7) + u8 seq + 5 * u32 = 25 bytes
        byte[] body = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(body,                PrivateUpdateVital.FullOpcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5),      vital);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9),      ranks);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13),     start);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17),     xp);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21),     current);
        return body;
    }

    private static byte[] BuildCurrent(byte seq, uint vital, uint current)
    {
        // u32 opcode (0x02E9) + u8 seq + 2 * u32 = 13 bytes
        byte[] body = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            PrivateUpdateVital.CurrentOpcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5),  vital);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9),  current);
        return body;
    }

    [Fact]
    public void TryParseFull_RoundTrip()
    {
        var bytes = BuildFull(seq: 12, vital: 2, ranks: 100, start: 12345, xp: 67890, current: 100);

        var p = PrivateUpdateVital.TryParseFull(bytes);

        Assert.NotNull(p);
        Assert.Equal((byte)12, p!.Value.Sequence);
        Assert.Equal(2u,       p.Value.VitalId);
        Assert.Equal(100u,     p.Value.Ranks);
        Assert.Equal(12345u,   p.Value.Start);
        Assert.Equal(67890u,   p.Value.Xp);
        Assert.Equal(100u,     p.Value.Current);
    }

    [Fact]
    public void TryParseCurrent_RoundTrip()
    {
        var bytes = BuildCurrent(seq: 12, vital: 2, current: 100);

        var p = PrivateUpdateVital.TryParseCurrent(bytes);

        Assert.NotNull(p);
        Assert.Equal((byte)12, p!.Value.Sequence);
        Assert.Equal(2u,       p.Value.VitalId);
        Assert.Equal(100u,     p.Value.Current);
    }

    [Fact]
    public void TryParseFull_RejectsWrongOpcode()
    {
        var bytes = BuildFull(seq: 12, vital: 2, ranks: 100, start: 12345, xp: 67890, current: 100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEAD_BEEFu);

        Assert.Null(PrivateUpdateVital.TryParseFull(bytes));
    }

    [Fact]
    public void TryParseCurrent_RejectsWrongOpcode()
    {
        var bytes = BuildCurrent(seq: 12, vital: 2, current: 100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xDEAD_BEEFu);

        Assert.Null(PrivateUpdateVital.TryParseCurrent(bytes));
    }

    [Fact]
    public void TryParseFull_RejectsTruncatedBody()
    {
        var full = BuildFull(seq: 12, vital: 2, ranks: 100, start: 0, xp: 0, current: 100);
        // 24 bytes — one short of 25.
        Assert.Null(PrivateUpdateVital.TryParseFull(full[..24]));
    }

    [Fact]
    public void TryParseCurrent_RejectsTruncatedBody()
    {
        var current = BuildCurrent(seq: 1, vital: 4, current: 50);
        // 12 bytes — one short of 13.
        Assert.Null(PrivateUpdateVital.TryParseCurrent(current[..12]));
    }

    [Fact]
    public void TryParseFull_ParsesStaminaAndManaIds()
    {
        var stam = PrivateUpdateVital.TryParseFull(
            BuildFull(seq: 7, vital: 4, ranks: 50, start: 100, xp: 0, current: 75));
        Assert.NotNull(stam);
        Assert.Equal(4u, stam!.Value.VitalId);
        Assert.Equal(75u, stam.Value.Current);

        var mana = PrivateUpdateVital.TryParseFull(
            BuildFull(seq: 8, vital: 6, ranks: 30, start: 80, xp: 0, current: 110));
        Assert.NotNull(mana);
        Assert.Equal(6u, mana!.Value.VitalId);
        Assert.Equal(110u, mana.Value.Current);
    }
}
