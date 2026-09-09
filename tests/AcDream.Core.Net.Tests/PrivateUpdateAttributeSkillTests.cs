using System.Buffers.Binary;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class PrivateUpdateAttributeSkillTests
{
    private static byte[] BuildAttribute(
        byte seq, uint attr, uint ranks, uint start, uint xp)
    {
        // u32 opcode (0x02E3) + u8 seq + 4 * u32 = 21 bytes
        byte[] body = new byte[21];
        BinaryPrimitives.WriteUInt32LittleEndian(body, PrivateUpdateAttribute.Opcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5),  attr);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9),  ranks);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), start);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17), xp);
        return body;
    }

    private static byte[] BuildSkill(
        byte seq,
        uint skillId,
        ushort ranks,
        ushort adjustPP,
        uint sac,
        uint xp,
        uint init,
        uint resistance,
        double lastUsed)
    {
        // u32 opcode (0x02DD) + u8 seq + u32 + 2*u16 + 4*u32 + f64 = 37 bytes
        byte[] body = new byte[37];
        BinaryPrimitives.WriteUInt32LittleEndian(body, PrivateUpdateSkill.Opcode);
        body[4] = seq;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5),  skillId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9),  ranks);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(11), adjustPP);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), sac);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17), xp);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21), init);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(25), resistance);
        BinaryPrimitives.WriteInt64LittleEndian(
            body.AsSpan(29), BitConverter.DoubleToInt64Bits(lastUsed));
        return body;
    }

    [Fact]
    public void Attribute_RoundTrip()
    {
        // A Quickness (3) raise: 41 ranks over a 100 start, 1,010,895 xp.
        var bytes = BuildAttribute(seq: 7, attr: 3, ranks: 41, start: 100, xp: 1_010_895);

        var p = PrivateUpdateAttribute.TryParse(bytes);

        Assert.NotNull(p);
        Assert.Equal((byte)7,     p!.Value.Sequence);
        Assert.Equal(3u,          p.Value.AttributeId);
        Assert.Equal(41u,         p.Value.Ranks);
        Assert.Equal(100u,        p.Value.Start);
        Assert.Equal(1_010_895u,  p.Value.Xp);
    }

    [Fact]
    public void Attribute_RejectsWrongOpcodeAndTruncation()
    {
        var bytes = BuildAttribute(1, 1, 1, 10, 100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x02E7u);
        Assert.Null(PrivateUpdateAttribute.TryParse(bytes));

        var good = BuildAttribute(1, 1, 1, 10, 100);
        Assert.Null(PrivateUpdateAttribute.TryParse(good.AsSpan(0, 20)));
    }

    [Fact]
    public void Skill_RoundTrip_PreservesAllFields()
    {
        var bytes = BuildSkill(
            seq: 12, skillId: 6, ranks: 50, adjustPP: 1, sac: 3,
            xp: 1000, init: 10, resistance: 0, lastUsed: 0.0);

        var p = PrivateUpdateSkill.TryParse(bytes);

        Assert.NotNull(p);
        Assert.Equal((byte)12, p!.Value.Sequence);
        Assert.Equal(6u,       p.Value.SkillId);
        Assert.Equal(50u,      p.Value.Ranks);
        Assert.Equal((ushort)1, p.Value.AdjustPP);
        Assert.Equal(3u,       p.Value.AdvancementClass);
        Assert.Equal(1000u,    p.Value.Xp);
        Assert.Equal(10u,      p.Value.Init);
        Assert.Equal(0u,       p.Value.Resistance);
        Assert.Equal(0.0,      p.Value.LastUsed);
    }

    [Fact]
    public void Skill_LastUsedSurvivesAsDoubleBits()
    {
        var bytes = BuildSkill(1, 14, 3, 1, 2, 42, 0, 5, 12345.678);

        var p = PrivateUpdateSkill.TryParse(bytes);

        Assert.NotNull(p);
        Assert.Equal(12345.678, p!.Value.LastUsed);
    }

    [Fact]
    public void Skill_RejectsWrongOpcodeAndTruncation()
    {
        var bytes = BuildSkill(1, 6, 1, 1, 2, 0, 0, 0, 0.0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x02DFu);
        Assert.Null(PrivateUpdateSkill.TryParse(bytes));

        var good = BuildSkill(1, 6, 1, 1, 2, 0, 0, 0, 0.0);
        Assert.Null(PrivateUpdateSkill.TryParse(good.AsSpan(0, 36)));
    }
}
