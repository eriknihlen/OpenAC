using System;
using System.Buffers.Binary;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class InventoryActionsTests
{
    [Fact]
    public void CreateTinkeringToolMatchesRetailToolThenPackableGuidList()
    {
        byte[] body = InventoryActions.BuildCreateTinkeringTool(
            7u,
            0x50000010u,
            [0x50000020u, 0x50000030u]);

        Assert.Equal(28, body.Length);
        Assert.Equal(InventoryActions.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(7u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(InventoryActions.CreateTinkeringToolOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x50000010u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(2u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(0x50000020u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(0x50000030u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
    }

    [Fact]
    public void BuildStackableMerge_CarriesBothGuidsAndAmount()
    {
        byte[] body = InventoryActions.BuildStackableMerge(
            seq: 1, mergeFromGuid: 0xAA, mergeToGuid: 0xBB, amount: 5);

        Assert.Equal(24, body.Length);
        Assert.Equal(InventoryActions.StackableMergeOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xAAu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0xBBu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(5u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildStackableSplitToContainer_FourFields()
    {
        byte[] body = InventoryActions.BuildStackableSplitToContainer(
            seq: 1, stackGuid: 0xAA, containerGuid: 0xBB, placement: 3, amount: 10);

        Assert.Equal(28, body.Length);
        Assert.Equal(InventoryActions.StackableSplitToContainerOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(3u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
        Assert.Equal(10u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)));
    }

    [Fact]
    public void BuildStackableSplitTo3D_TwoFields()
    {
        byte[] body = InventoryActions.BuildStackableSplitTo3D(
            seq: 1, stackGuid: 0xAA, amount: 3);
        Assert.Equal(20, body.Length);
        Assert.Equal(InventoryActions.StackableSplitTo3DOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildStackableSplitToWield_HasEquipLocation()
    {
        byte[] body = InventoryActions.BuildStackableSplitToWield(
            seq: 1, stackGuid: 0xAA, equipLocation: 0x00400000, amount: 1);
        Assert.Equal(0x00400000u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildGiveObjectRequest_TargetItemAmount()
    {
        byte[] body = InventoryActions.BuildGiveObjectRequest(
            seq: 1, targetGuid: 0xAA, itemGuid: 0xBB, amount: 2);
        Assert.Equal(InventoryActions.GiveObjectRequestOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0xAAu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0xBBu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(2u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildAddShortcut_ItemShortcut_FieldLayout()
    {
        var entry = new ShortcutEntry(Index: 0, ObjectId: 0x3E1u, SpellId: 0u);
        byte[] body = InventoryActions.BuildAddShortcut(seq: 1, entry);
        Assert.Equal(24, body.Length);
        Assert.Equal(InventoryActions.AddShortcutOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0,       BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0x3E1u,  BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(0u,      BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildAddShortcut_PreservesSignedIndexAndRawSpellWord()
    {
        var entry = new ShortcutEntry(Index: -1, ObjectId: 0u, SpellId: 0xA5C31234u);
        byte[] body = InventoryActions.BuildAddShortcut(seq: 1, entry);
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(0xA5C31234u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)));
    }

    [Fact]
    public void BuildRemoveShortcut_HasSlotOnly()
    {
        byte[] body = InventoryActions.BuildRemoveShortcut(seq: 1, slotIndex: 5);
        Assert.Equal(16, body.Length);
        Assert.Equal(5u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildTeleToPoi_SimpleIdPayload()
    {
        byte[] body = InventoryActions.BuildTeleToPoi(seq: 1, poiId: 42);
        Assert.Equal(InventoryActions.TeleToPoiOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(42u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void BuildDropItem_layout()
    {
        byte[] b = InventoryActions.BuildDropItem(seq: 7, itemGuid: 0x50000A01u);
        Assert.Equal(16, b.Length);
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0)));
        Assert.Equal(7u,      BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(0x001Bu, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(0x50000A01u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
    }

    [Fact]
    public void BuildGetAndWieldItem_layout()
    {
        byte[] b = InventoryActions.BuildGetAndWieldItem(seq: 7, itemGuid: 0x50000A01u, equipMask: 0x02u);
        Assert.Equal(20, b.Length);
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0)));
        Assert.Equal(7u,      BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(0x001Au, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(0x50000A01u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
        Assert.Equal(0x02u,   BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)));
    }

    [Fact]
    public void BuildNoLongerViewingContents_layout()
    {
        byte[] b = InventoryActions.BuildNoLongerViewingContents(seq: 7, containerGuid: 0x500000C9u);
        Assert.Equal(16, b.Length);
        Assert.Equal(0xF7B1u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0)));
        Assert.Equal(7u,      BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal(0x0195u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(0x500000C9u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12)));
    }

    [Fact]
    public void BuildSetInscription_UsesRetailOpcodeGuidAndCp1252String16L()
    {
        byte[] body = InventoryActions.BuildSetInscription(
            seq: 7,
            itemGuid: 0x50000A01u,
            inscription: "Café");

        Assert.Equal(24, body.Length);
        Assert.Equal(0xF7B1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(7u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(0x00BFu,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x50000A01u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(4,
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(16)));
        Assert.Equal(new byte[] { 0x43, 0x61, 0x66, 0xE9 },
            body.AsSpan(18, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0 }, body.AsSpan(22, 2).ToArray());
    }

    [Fact]
    public void BuildSetInscription_EmptyTextPacksAlignedClearRequest()
    {
        byte[] body = InventoryActions.BuildSetInscription(
            seq: 1,
            itemGuid: 0x50000A01u,
            inscription: string.Empty);

        Assert.Equal(20, body.Length);
        Assert.Equal(0,
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(16)));
        Assert.Equal(new byte[] { 0, 0 }, body.AsSpan(18, 2).ToArray());
    }
}
