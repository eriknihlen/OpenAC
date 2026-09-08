using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class ToolbarAmmoPolicyTests
{
    [Fact]
    public void StackableMissileWeapon_isItsOwnAmmo()
    {
        var thrown = Item(1u, EquipMask.MissileWeapon, stack: 7, maxStack: 25);
        var separateAmmo = Item(2u, EquipMask.MissileAmmo, stack: 80, maxStack: 100);

        ToolbarAmmoPolicy.Result result = ToolbarAmmoPolicy.Resolve(
            new[] { thrown, separateAmmo });

        Assert.Equal(new ToolbarAmmoPolicy.Result(1u, 7), result);
    }

    [Fact]
    public void NonStackableLauncher_usesSeparateAmmoSlot()
    {
        var bow = Item(1u, EquipMask.MissileWeapon, stack: 1, maxStack: 1);
        var arrows = Item(2u, EquipMask.MissileAmmo, stack: 42, maxStack: 100);

        ToolbarAmmoPolicy.Result result = ToolbarAmmoPolicy.Resolve(
            new[] { bow, arrows });

        Assert.Equal(new ToolbarAmmoPolicy.Result(2u, 42), result);
    }

    [Fact]
    public void MissingAmmo_isHidden_andWireZeroStackDisplaysOne()
    {
        Assert.Equal(default, ToolbarAmmoPolicy.Resolve(Array.Empty<ClientObject>()));

        var arrows = Item(3u, EquipMask.MissileAmmo, stack: 0, maxStack: 100);
        Assert.Equal(
            new ToolbarAmmoPolicy.Result(3u, 1),
            ToolbarAmmoPolicy.Resolve(new[] { arrows }));
    }

    [Fact]
    public void FirstIntersectingInventoryPlacementWins()
    {
        var first = Item(1u, EquipMask.MissileAmmo | EquipMask.Held, stack: 10, maxStack: 100);
        var second = Item(2u, EquipMask.MissileAmmo, stack: 20, maxStack: 100);

        Assert.Equal(
            new ToolbarAmmoPolicy.Result(1u, 10),
            ToolbarAmmoPolicy.Resolve(new[] { first, second }));
    }

    private static ClientObject Item(
        uint id,
        EquipMask location,
        int stack,
        int maxStack)
        => new()
        {
            ObjectId = id,
            CurrentlyEquippedLocation = location,
            StackSize = stack,
            StackSizeMax = maxStack,
        };
}
