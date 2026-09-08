using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class PaperdollSelectionPolicyTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void UncoveredValidBodyPart_returnsPlayer()
    {
        var objects = TableWithPlayer();

        uint selected = PaperdollSelectionPolicy.GetUpperInventoryObject(
            objects,
            Player,
            EquipMask.HeadWear);

        Assert.Equal(Player, selected);
    }

    [Fact]
    public void DefaultPriorityArmor_beatsClothingOnSameBodyPart()
    {
        var objects = TableWithPlayer();
        AddEquipped(objects, 0x50001001u, EquipMask.ChestWear, priority: 20u);
        AddEquipped(objects, 0x50001002u, EquipMask.ChestArmor, priority: 0u);

        uint selected = PaperdollSelectionPolicy.GetUpperInventoryObject(
            objects,
            Player,
            EquipMask.ChestWear | EquipMask.ChestArmor);

        Assert.Equal(0x50001002u, selected);
    }

    [Fact]
    public void HigherExplicitPriorityWins_andTiesPreserveListOrder()
    {
        var objects = TableWithPlayer();
        AddEquipped(objects, 0x50001001u, EquipMask.HeadWear, priority: 12u);
        AddEquipped(objects, 0x50001002u, EquipMask.HeadWear, priority: 12u);
        AddEquipped(objects, 0x50001003u, EquipMask.HeadWear, priority: 13u);

        uint selected = PaperdollSelectionPolicy.GetUpperInventoryObject(
            objects,
            Player,
            EquipMask.HeadWear);

        Assert.Equal(0x50001003u, selected);

        objects.Remove(0x50001003u);
        selected = PaperdollSelectionPolicy.GetUpperInventoryObject(
            objects,
            Player,
            EquipMask.HeadWear);
        Assert.Equal(0x50001001u, selected);
    }

    [Fact]
    public void DefaultArmorPriority_usesCoveredIntersectionRangeExactly()
    {
        var objects = TableWithPlayer();
        AddEquipped(objects, 0x50001001u, EquipMask.LowerLegWear, priority: 1u);
        AddEquipped(
            objects,
            0x50001002u,
            EquipMask.LowerLegWear | EquipMask.LowerLegArmor,
            priority: 0u);

        uint selected = PaperdollSelectionPolicy.GetUpperInventoryObject(
            objects,
            Player,
            EquipMask.LowerLegWear | EquipMask.LowerLegArmor);

        Assert.Equal(0x50001001u, selected);
    }

    [Fact]
    public void InvalidBodyMask_returnsZero()
    {
        var objects = TableWithPlayer();

        Assert.Equal(0u, PaperdollSelectionPolicy.GetUpperInventoryObject(
            objects,
            Player,
            EquipMask.None));
    }

    private static ClientObjectTable TableWithPlayer()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = Player, Type = ItemType.Creature });
        return objects;
    }

    private static void AddEquipped(
        ClientObjectTable objects,
        uint objectId,
        EquipMask location,
        uint priority)
    {
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = objectId,
            WielderId = Player,
            CurrentlyEquippedLocation = location,
            Priority = priority,
        });
    }
}
