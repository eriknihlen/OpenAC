using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AcDream.Core.Net.Messages;

public static class EnchantmentWireReader
{
    public static PlayerDescriptionParser.EnchantmentEntry Read(
        ReadOnlySpan<byte> source,
        ref int position,
        PlayerDescriptionParser.EnchantmentBucket bucket = 0)
    {
        if (source.Length - position < 60)
            throw new FormatException("truncated enchantment record");

        ushort spellId = ReadU16(source, ref position);
        ushort layer = ReadU16(source, ref position);
        ushort spellCategory = ReadU16(source, ref position);
        ushort hasSpellSetId = ReadU16(source, ref position);
        uint powerLevel = ReadU32(source, ref position);
        double startTime = ReadF64(source, ref position);
        double duration = ReadF64(source, ref position);
        uint casterGuid = ReadU32(source, ref position);
        float degradeModifier = ReadF32(source, ref position);
        float degradeLimit = ReadF32(source, ref position);
        double lastDegraded = ReadF64(source, ref position);
        uint statModType = ReadU32(source, ref position);
        uint statModKey = ReadU32(source, ref position);
        float statModValue = ReadF32(source, ref position);
        uint? spellSetId = hasSpellSetId != 0 ? ReadU32(source, ref position) : null;

        if (!double.IsFinite(startTime)
            || !double.IsFinite(duration)
            || !float.IsFinite(degradeModifier)
            || !float.IsFinite(degradeLimit)
            || !double.IsFinite(lastDegraded)
            || !float.IsFinite(statModValue))
        {
            throw new FormatException("non-finite enchantment value");
        }

        return new PlayerDescriptionParser.EnchantmentEntry(
            spellId, layer, spellCategory, hasSpellSetId, powerLevel,
            startTime, duration, casterGuid, degradeModifier, degradeLimit,
            lastDegraded, statModType, statModKey, statModValue, spellSetId,
            bucket);
    }

    public static IReadOnlyList<PlayerDescriptionParser.EnchantmentEntry> ReadList(
        ReadOnlySpan<byte> source,
        ref int position,
        PlayerDescriptionParser.EnchantmentBucket bucket = 0)
    {
        if (source.Length - position < 4)
            throw new FormatException("truncated enchantment list count");

        uint count = ReadU32(source, ref position);
        if (count > 0x4000)
            throw new FormatException("unreasonable enchantment list count");

        var result = new List<PlayerDescriptionParser.EnchantmentEntry>((int)count);
        for (uint i = 0; i < count; i++)
            result.Add(Read(source, ref position, bucket));
        return result;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> source, ref int position)
    {
        Require(source, position, 2);
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(position, 2));
        position += 2;
        return value;
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, ref int position)
    {
        Require(source, position, 4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(position, 4));
        position += 4;
        return value;
    }

    private static float ReadF32(ReadOnlySpan<byte> source, ref int position)
    {
        Require(source, position, 4);
        float value = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(position, 4));
        position += 4;
        return value;
    }

    private static double ReadF64(ReadOnlySpan<byte> source, ref int position)
    {
        Require(source, position, 8);
        double value = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(position, 8));
        position += 8;
        return value;
    }

    private static void Require(ReadOnlySpan<byte> source, int position, int count)
    {
        if (position < 0 || source.Length - position < count)
            throw new FormatException("truncated enchantment record");
    }
}
