using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class GameEventsInventoryTests
{
    [Fact]
    public void ParseViewContents_twoEntries_returnsContainerAndItems()
    {
        var b = new byte[4 + 4 + 2 * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x500000C9u);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x50000A01u); // guid 1
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 0u);         // type 1 (item)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 0x50000A02u);// guid 2
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1u);

        var p = GameEvents.ParseViewContents(b);
        Assert.NotNull(p);
        Assert.Equal(0x500000C9u, p!.Value.ContainerGuid);
        Assert.Equal(2, p.Value.Items.Count);
        Assert.Equal(0x50000A01u, p.Value.Items[0].Guid);
        Assert.Equal(0u, p.Value.Items[0].ContainerType);
        Assert.Equal(0x50000A02u, p.Value.Items[1].Guid);
        Assert.Equal(1u, p.Value.Items[1].ContainerType);
    }

    [Fact]
    public void ParseViewContents_zeroCount_returnsEmptyList()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x500000C9u);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0u);
        var p = GameEvents.ParseViewContents(b);
        Assert.NotNull(p);
        Assert.Empty(p!.Value.Items);
    }

    [Fact]
    public void ParseViewContents_truncated_returnsNull()
        => Assert.Null(GameEvents.ParseViewContents(new byte[4]));

    [Fact]
    public void ParsePutObjInContainer_readsAllFourFields()
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x50000A01u); // itemGuid
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x500000C9u);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 3u);          // placement
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 1u);

        var p = GameEvents.ParsePutObjInContainer(b);
        Assert.NotNull(p);
        Assert.Equal(0x50000A01u, p!.Value.ItemGuid);
        Assert.Equal(0x500000C9u, p.Value.ContainerGuid);
        Assert.Equal(3u, p.Value.Placement);
        Assert.Equal(1u, p.Value.ContainerType);
    }

    [Fact]
    public void ParseInventoryServerSaveFailed_readsGuidAndError()
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x50000A01u); // itemGuid
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 0x0Au);       // weenieError

        var p = GameEvents.ParseInventoryServerSaveFailed(b);
        Assert.NotNull(p);
        Assert.Equal(0x50000A01u, p!.Value.ItemGuid);
        Assert.Equal(0x0Au, p.Value.WeenieError);
    }

    [Fact]
    public void ParseSalvageOperationsResult_readsRejectedItemsAndResults()
    {
        var wire = new AceWireWriter()
            .Write(28u)
            .Write(1u).Write(0x50000020u)
            .Write(1u)
            .Write(63u).Write(unchecked((ulong)BitConverter.DoubleToInt64Bits(8d))).Write(1u)
            .Write(25)
            .ToArray();

        GameEvents.SalvageOperationsResult? result =
            GameEvents.ParseSalvageOperationsResult(wire);

        Assert.NotNull(result);
        Assert.Equal(28u, result!.Value.SkillId);
        Assert.Equal([0x50000020u], result.Value.UnsuitableItemGuids);
        GameEvents.SalvageResult material = Assert.Single(result.Value.Results);
        Assert.Equal((63u, 8d, 1u), (material.MaterialType, material.Workmanship, material.Units));
        Assert.Equal(25, result.Value.AugmentationBonusPercent);
    }

    [Fact]
    public void ParseSalvageOperationsResult_truncatedResult_returnsNull()
    {
        var wire = new AceWireWriter()
            .Write(28u).Write(0u).Write(1u).Write(63u)
            .ToArray();

        Assert.Null(GameEvents.ParseSalvageOperationsResult(wire));
    }
}
