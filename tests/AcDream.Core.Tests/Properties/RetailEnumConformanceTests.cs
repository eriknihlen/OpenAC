using System;
using System.Linq;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Properties;

public sealed class RetailEnumConformanceTests
{
    public static TheoryData<string, uint> RetailDamageType => new()
    {
        { "Undef", 0x0 },
        { "Slash", 0x1 },
        { "Pierce", 0x2 },
        { "Bludgeon", 0x4 },
        { "Cold", 0x8 },
        { "Fire", 0x10 },
        { "Acid", 0x20 },
        { "Electric", 0x40 },
        { "Health", 0x80 },
        { "Stamina", 0x100 },
        { "Mana", 0x200 },
        { "Nether", 0x400 },
        { "Base", 0x10000000 },
    };

    [Theory]
    [MemberData(nameof(RetailDamageType))]
    public void DamageTypeMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(DamageType), name),
            $"DamageType.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<DamageType>(name));
    }

    [Fact]
    public void DamageTypeDeclaresNothingRetailDoesNot()
    {
        var expected = RetailDamageType.Select(r => (string)r[0]).OrderBy(n => n, StringComparer.Ordinal);
        var actual = Enum.GetNames<DamageType>().OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string, uint> RetailItemType => new()
    {
        { "None", 0x0 },
        { "MeleeWeapon", 0x1 },
        { "Armor", 0x2 },
        { "Vestements", 0x6 },
        { "Clothing", 0x4 },
        { "Jewelry", 0x8 },
        { "Creature", 0x10 },
        { "Food", 0x20 },
        { "Money", 0x40 },
        { "Misc", 0x80 },
        { "MissileWeapon", 0x100 },
        { "Weapon", 0x101 },
        { "Container", 0x200 },
        { "LockableMagicTarget", 0x280 },
        { "Useless", 0x400 },
        { "Gem", 0x800 },
        { "SpellComponents", 0x1000 },
        { "Writable", 0x2000 },
        { "Key", 0x4000 },
        { "Caster", 0x8000 },
        { "WeaponOrCaster", 0x8101 },
        { "RedirectableItemEnchantmentTarget", 0x8107 },
        { "Portal", 0x10000 },
        { "Lockable", 0x20000 },
        { "PromissoryNote", 0x40000 },
        { "ManaStone", 0x80000 },
        { "ItemEnchantableTarget", 0x88B8F },
        { "Service", 0x100000 },
        { "MagicWieldable", 0x200000 },
        { "Item", 0x2DFBEF },
        { "CraftCookingBase", 0x400000 },
        { "VendorGrocer", 0x446220 },
        { "CraftAlchemyBase", 0x800000 },
        { "CraftFletchingBase", 0x1000000 },
        { "CraftAlchemyIntermediate", 0x4000000 },
        { "CraftFletchingIntermediate", 0x8000000 },
        { "LifeStone", 0x10000000 },
        { "PortalMagicTarget", 0x10010000 },
        { "TinkeringTool", 0x20000000 },
        { "TinkeringMaterial", 0x40000000 },
        { "VendorShopkeep", 0x480467A7 },
        { "Gameboard", 0x80000000 },
    };

    [Theory]
    [MemberData(nameof(RetailItemType))]
    public void ItemTypeMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(ItemType), name),
            $"ItemType.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<ItemType>(name));
    }

    [Fact]
    public void ItemTypeDeclaresNothingRetailDoesNot()
    {
        var expected = RetailItemType.Select(r => (string)r[0]).OrderBy(n => n, StringComparer.Ordinal);
        var actual = Enum.GetNames<ItemType>().OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CraftLadderMatchesRetailAndLeavesTheUnusedBitUnclaimed()
    {
        Assert.Equal(0x00400000u, (uint)ItemType.CraftCookingBase);
        Assert.Equal(0x00800000u, (uint)ItemType.CraftAlchemyBase);
        Assert.Equal(0x01000000u, (uint)ItemType.CraftFletchingBase);
        Assert.Equal(0x04000000u, (uint)ItemType.CraftAlchemyIntermediate);
        Assert.Equal(0x08000000u, (uint)ItemType.CraftFletchingIntermediate);
        Assert.DoesNotContain(Enum.GetValues<ItemType>(), t => (uint)t == 0x02000000u);
    }

    [Fact]
    public void WeaponExcludesCasterAndWeaponOrCasterIncludesIt()
    {
        Assert.NotEqual(ItemType.Weapon, ItemType.WeaponOrCaster);
        Assert.Equal(ItemType.MeleeWeapon | ItemType.MissileWeapon, ItemType.Weapon);
        Assert.Equal(ItemType.Weapon | ItemType.Caster, ItemType.WeaponOrCaster);
    }

    public static TheoryData<string, uint> RetailEquipMaskComposites => new()
    {
        { "Clothing", 0x080001FF },
        { "Armor", 0x00007E00 },
        { "Jewelry", 0x7C0F8000 },
        { "WristWear", 0x00030000 },
        { "FingerWear", 0x000C0000 },
        { "Sigil", 0x70000000 },
        { "ReadySlot", 0x03F00000 },
        { "Weapon", 0x02500000 },
        { "WeaponReadySlot", 0x03500000 },
        { "All", 0x7FFFFFFF },
        { "CanGoInReadySlot", 0x7FFFFFFF },
    };

    [Theory]
    [MemberData(nameof(RetailEquipMaskComposites))]
    public void EquipMaskCompositeMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(EquipMask), name), $"EquipMask.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<EquipMask>(name));
    }

    [Fact]
    public void ClothingCompositeIsTheWearSlotsPlusCloak()
    {
        EquipMask wearSlots =
            EquipMask.HeadWear | EquipMask.ChestWear | EquipMask.AbdomenWear
            | EquipMask.UpperArmWear | EquipMask.LowerArmWear | EquipMask.HandWear
            | EquipMask.UpperLegWear | EquipMask.LowerLegWear | EquipMask.FootWear;

        Assert.Equal(0x000001FFu, (uint)wearSlots);
        Assert.Equal(EquipMask.Clothing, wearSlots | EquipMask.Cloak);
        Assert.NotEqual(EquipMask.Clothing, wearSlots);
        Assert.Equal(0u, (uint)EquipMask.Clothing & 0x80000000u);
    }

    public static TheoryData<string, uint> RetailTransientState => new()
    {
        { "Contact", 0x1 },
        { "OnWalkable", 0x2 },
        { "Sliding", 0x4 },
        { "WaterContact", 0x8 },
        { "StationaryFall", 0x10 },
        { "StationaryStop", 0x20 },
        { "StationaryStuck", 0x40 },
        { "Active", 0x80 },
        { "CheckEthereal", 0x100 },
    };

    [Theory]
    [MemberData(nameof(RetailTransientState))]
    public void TransientStateFlagsMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(TransientStateFlags), name),
            $"TransientStateFlags.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<TransientStateFlags>(name));
    }

    public static TheoryData<string, uint> RetailPhysicsState => new()
    {
        { "Static", 0x1 },
        { "ReservedUnused1", 0x2 },
        { "Ethereal", 0x4 },
        { "ReportCollisions", 0x8 },
        { "IgnoreCollisions", 0x10 },
        { "NoDraw", 0x20 },
        { "Missile", 0x40 },
        { "Pushable", 0x80 },
        { "AlignPath", 0x100 },
        { "PathClipped", 0x200 },
        { "Gravity", 0x400 },
        { "Lighting", 0x800 },
        { "ParticleEmitter", 0x1000 },
        { "ReservedUnused2", 0x2000 },
        { "Hidden", 0x4000 },
        { "ScriptedCollision", 0x8000 },
        { "HasPhysicsBsp", 0x10000 },
        { "Inelastic", 0x20000 },
        { "HasDefaultAnim", 0x40000 },
        { "HasDefaultScript", 0x80000 },
        { "Cloaked", 0x100000 },
        { "ReportAsEnvironment", 0x200000 },
        { "EdgeSlide", 0x400000 },
        { "Sledding", 0x800000 },
        { "Frozen", 0x1000000 },
    };

    [Theory]
    [MemberData(nameof(RetailPhysicsState))]
    public void PhysicsStateFlagsMatchesRetail(string name, uint value)
    {
        Assert.True(Enum.IsDefined(typeof(PhysicsStateFlags), name),
            $"PhysicsStateFlags.{name} is missing");
        Assert.Equal(value, (uint)Enum.Parse<PhysicsStateFlags>(name));
    }

    [Theory]
    [InlineData("Undef", 0)]
    [InlineData("High", 1)]
    [InlineData("Medium", 2)]
    [InlineData("Low", 3)]
    public void AttackHeightMatchesRetail(string name, int value)
    {
        Assert.True(Enum.IsDefined(typeof(AttackHeight), name), $"AttackHeight.{name} is missing");
        Assert.Equal(value, (int)Enum.Parse<AttackHeight>(name));
    }

    [Fact]
    public void AttackTypeCompositesMatchRetailLiterals()
    {
        Assert.Equal(0x19u, (uint)AttackType.Unarmed);
        Assert.Equal(0x79E0u, (uint)AttackType.MultiStrike);
    }
}
