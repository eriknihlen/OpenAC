using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class EquipMaskTests
{
    [Theory]
    [InlineData(0x00000000u, EquipMask.None)]
    [InlineData(0x00000001u, EquipMask.HeadWear)]
    [InlineData(0x00000002u, EquipMask.ChestWear)]
    [InlineData(0x00000004u, EquipMask.AbdomenWear)]
    [InlineData(0x00000008u, EquipMask.UpperArmWear)]
    [InlineData(0x00000010u, EquipMask.LowerArmWear)]
    [InlineData(0x00000020u, EquipMask.HandWear)]
    [InlineData(0x00000040u, EquipMask.UpperLegWear)]
    [InlineData(0x00000080u, EquipMask.LowerLegWear)]
    [InlineData(0x00000100u, EquipMask.FootWear)]
    [InlineData(0x00000200u, EquipMask.ChestArmor)]
    [InlineData(0x00000400u, EquipMask.AbdomenArmor)]
    [InlineData(0x00000800u, EquipMask.UpperArmArmor)]
    [InlineData(0x00001000u, EquipMask.LowerArmArmor)]
    [InlineData(0x00002000u, EquipMask.UpperLegArmor)]
    [InlineData(0x00004000u, EquipMask.LowerLegArmor)]
    [InlineData(0x00008000u, EquipMask.NeckWear)]
    [InlineData(0x00010000u, EquipMask.WristWearLeft)]
    [InlineData(0x00020000u, EquipMask.WristWearRight)]
    [InlineData(0x00040000u, EquipMask.FingerWearLeft)]
    [InlineData(0x00080000u, EquipMask.FingerWearRight)]
    [InlineData(0x00100000u, EquipMask.MeleeWeapon)]
    [InlineData(0x00200000u, EquipMask.Shield)]
    [InlineData(0x00400000u, EquipMask.MissileWeapon)]
    [InlineData(0x00800000u, EquipMask.MissileAmmo)]
    [InlineData(0x01000000u, EquipMask.Held)]
    [InlineData(0x02000000u, EquipMask.TwoHanded)]
    [InlineData(0x04000000u, EquipMask.TrinketOne)]
    [InlineData(0x08000000u, EquipMask.Cloak)]
    [InlineData(0x10000000u, EquipMask.SigilOne)]
    [InlineData(0x20000000u, EquipMask.SigilTwo)]
    [InlineData(0x40000000u, EquipMask.SigilThree)]
    public void Member_has_canonical_retail_value(uint expected, EquipMask member)
        => Assert.Equal(expected, (uint)member);

    [Fact]
    public void Weapon_ready_slot_composite_is_0x3500000()
        => Assert.Equal(0x3500000u,
            (uint)(EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.TwoHanded));
}
