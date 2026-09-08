using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CastSpellTests
{
    [Fact]
    public void BuildUntargeted_EmitsCorrectWireBytes()
    {
        byte[] body = CastSpellRequest.BuildUntargeted(gameActionSequence: 5, spellId: 0x3E1);

        Assert.Equal(16, body.Length);
        Assert.Equal(CastSpellRequest.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(5u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(CastSpellRequest.UntargetedSubOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x3E1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildTargeted_EmitsCorrectWireBytes()
    {
        byte[] body = CastSpellRequest.BuildTargeted(
            gameActionSequence: 7, targetGuid: 0xAAAAu, spellId: 0x3E1);

        Assert.Equal(20, body.Length);
        Assert.Equal(CastSpellRequest.TargetedSubOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xAAAAu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0x3E1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void ParseMagicUpdateEnchantment_RoundTrip()
    {
        byte[] payload = BuildEnchantment(spellId: 42, layer: 7, duration: 300, caster: 0xBEEF);

        var parsed = GameEvents.ParseMagicUpdateEnchantment(payload);
        Assert.NotNull(parsed);
        Assert.Equal((ushort)42, parsed!.Value.SpellId);
        Assert.Equal((ushort)7, parsed.Value.Layer);
        Assert.Equal(300d, parsed.Value.Duration, 4);
        Assert.Equal(0xBEEFu, parsed.Value.CasterGuid);
        Assert.Equal(0x20u, parsed.Value.StatModType);
        Assert.Equal(7u, parsed.Value.StatModKey);
        Assert.Equal(1.25f, parsed.Value.StatModValue);
    }

    [Fact]
    public void ParseMagicRemoveEnchantment_RoundTrip()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 42);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 7);

        var parsed = GameEvents.ParseMagicRemoveEnchantment(payload);
        Assert.NotNull(parsed);
        Assert.Equal((ushort)7, parsed!.Value.Layer);
        Assert.Equal((ushort)42, parsed.Value.SpellId);
    }

    [Fact]
    public void ParseMagicUpdateMultipleEnchantments_ReadsEveryRecord()
    {
        byte[] first = BuildEnchantment(42, 1, 60, 0xAA);
        byte[] second = BuildEnchantment(43, 2, 120, 0xBB);
        byte[] payload = new byte[4 + first.Length + second.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 2);
        first.CopyTo(payload, 4);
        second.CopyTo(payload, 4 + first.Length);

        var parsed = GameEvents.ParseMagicUpdateMultipleEnchantments(payload);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal((ushort)42, parsed[0].SpellId);
        Assert.Equal((ushort)43, parsed[1].SpellId);
    }

    [Fact]
    public void ParseMagicUpdateEnchantment_RejectsTruncatedOptionalSpellSetId()
    {
        byte[] payload = BuildEnchantment(42, 1, 60, 0xAA);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 1);

        Assert.Null(GameEvents.ParseMagicUpdateEnchantment(payload));
    }

    [Fact]
    public void ParseMagicLayeredSpellList_RejectsTruncation()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 1);

        Assert.Null(GameEvents.ParseMagicLayeredSpellList(payload));
    }

    private static byte[] BuildEnchantment(
        ushort spellId, ushort layer, double duration, uint caster)
    {
        byte[] payload = new byte[60];
        int offset = 0;
        WriteU16(spellId); WriteU16(layer); WriteU16(3); WriteU16(0);
        WriteU32(8); WriteF64(10); WriteF64(duration); WriteU32(caster);
        WriteF32(0.1f); WriteF32(-1f); WriteF64(0);
        WriteU32(0x20); WriteU32(7); WriteF32(1.25f);
        return payload;

        void WriteU16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), value); offset += 2; }
        void WriteU32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value); offset += 4; }
        void WriteF32(float value) { BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset), value); offset += 4; }
        void WriteF64(double value) { BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(offset), value); offset += 8; }
    }
}
