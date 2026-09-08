using System;
using System.Linq;
using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Properties;

public sealed class ItemWireEnumConformanceTests
{
    public static TheoryData<string, uint> RetailAmmoType => new()
    {
        { "None", 0x0 },
        { "Arrow", 0x1 },
        { "Bolt", 0x2 },
        { "Atlatl", 0x4 },
        { "ArrowCrystal", 0x8 },
        { "BoltCrystal", 0x10 },
        { "AtlatlCrystal", 0x20 },
        { "ArrowChorizite", 0x40 },
        { "BoltChorizite", 0x80 },
        { "AtlatlChorizite", 0x100 },
    };

    [Theory]
    [MemberData(nameof(RetailAmmoType))]
    public void AmmoTypeMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(AmmoType), name), $"AmmoType.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<AmmoType>(name));
    }

    [Fact]
    public void AmmoTypeDeclaresNothingRetailDoesNot()
    {
        var expected = RetailAmmoType.Select(r => (string)r[0]).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, Enum.GetNames<AmmoType>().OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("None", 0u)]
    [InlineData("Melee", 1u)]
    [InlineData("Missile", 2u)]
    [InlineData("Ammo", 3u)]
    [InlineData("Shield", 4u)]
    [InlineData("TwoHanded", 5u)]
    public void CombatUseMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(CombatUse), name), $"CombatUse.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<CombatUse>(name));
    }

    public static TheoryData<string, uint> RetailItemUseable => new()
    {
        { "Undef", 0x0 },
        { "No", 0x1 },
        { "Self", 0x2 },
        { "Wielded", 0x4 },
        { "Contained", 0x8 },
        { "Viewed", 0x10 },
        { "ContainedViewed", 0x18 },
        { "Remote", 0x20 },
        { "ViewedRemote", 0x30 },
        { "ContainedViewedRemote", 0x38 },
        { "NeverWalk", 0x40 },
        { "RemoteNeverWalk", 0x60 },
        { "ViewedRemoteNeverWalk", 0x70 },
        { "ContainedViewedRemoteNeverWalk", 0x78 },
        { "ObjSelf", 0x80 },
        { "SourceMask", 0xFFFF },
        { "SourceWieldedTargetWielded", 0x40004 },
        { "SourceContainedTargetWielded", 0x40008 },
        { "SourceViewedTargetWielded", 0x40010 },
        { "SourceRemoteTargetWielded", 0x40020 },
        { "SourceWieldedTargetContained", 0x80004 },
        { "SourceContainedTargetContained", 0x80008 },
        { "SourceViewedTargetContained", 0x80010 },
        { "SourceRemoteTargetContained", 0x80020 },
        { "SourceContainedTargetSelfOrContained", 0xA0008 },
        { "SourceWieldedTargetViewed", 0x100004 },
        { "SourceContainedTargetViewed", 0x100008 },
        { "SourceViewedTargetViewed", 0x100010 },
        { "SourceRemoteTargetViewed", 0x100020 },
        { "SourceWieldedTargetRemote", 0x200004 },
        { "SourceContainedTargetRemote", 0x200008 },
        { "SourceViewedTargetRemote", 0x200010 },
        { "SourceRemoteTargetRemote", 0x200020 },
        { "SourceContainedTargetRemoteOrSelf", 0x220008 },
        { "SourceWieldedTargetRemoteNeverWalk", 0x600004 },
        { "SourceContainedTargetRemoteNeverWalk", 0x600008 },
        { "SourceRemoteTargetRemoteNeverWalk", 0x600020 },
        { "SourceContainedTargetObjSelfOrContained", 0x880008 },
        { "TargetMask", 0xFFFF0000 },
    };

    [Theory]
    [MemberData(nameof(RetailItemUseable))]
    public void ItemUseableMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(ItemUseable), name), $"ItemUseable.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<ItemUseable>(name));
    }

    [Fact]
    public void ItemUseableDeclaresNothingRetailDoesNot()
    {
        var expected = RetailItemUseable.Select(r => (string)r[0]).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, Enum.GetNames<ItemUseable>().OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ItemUseableSourceAndTargetMasksPartitionTheWord()
    {
        Assert.Equal(0u, (uint)ItemUseable.SourceMask & (uint)ItemUseable.TargetMask);
        Assert.Equal(uint.MaxValue, (uint)ItemUseable.SourceMask | (uint)ItemUseable.TargetMask);

        uint naiveUnion = (uint)ItemUseable.ObjSelf | (uint)ItemUseable.Contained
                          | ((uint)ItemUseable.Contained << 16);
        Assert.NotEqual((uint)ItemUseable.SourceContainedTargetObjSelfOrContained, naiveUnion);
    }
}
