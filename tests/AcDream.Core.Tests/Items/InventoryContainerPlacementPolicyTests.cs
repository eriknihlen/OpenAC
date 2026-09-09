using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class InventoryContainerPlacementPolicyTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void FullItemCapacityRejectsNewItemButAllowsReorder()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = "Player",
            ItemsCapacity = 1,
            ContainersCapacity = 7,
        });
        objects.AddOrUpdate(new ClientObject { ObjectId = 2u });
        objects.MoveItem(2u, Player, 0);
        objects.AddOrUpdate(new ClientObject { ObjectId = 3u });

        Assert.Equal(
            InventoryContainerPlacementRejection.ItemCapacityFull,
            InventoryContainerPlacementPolicy.Evaluate(objects, 3u, Player, Player));
        Assert.Equal(
            InventoryContainerPlacementRejection.None,
            InventoryContainerPlacementPolicy.Evaluate(objects, 2u, Player, Player));
    }

    [Fact]
    public void ContainerCycleAndTradeAreRejected()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 10u, Type = ItemType.Container, ItemsCapacity = 24,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 11u, Type = ItemType.Container, ItemsCapacity = 24,
        });
        objects.MoveItem(11u, 10u, 0);

        Assert.Equal(
            InventoryContainerPlacementRejection.RecursiveContainment,
            InventoryContainerPlacementPolicy.Evaluate(objects, 10u, 11u, Player));

        objects.Get(10u)!.TradeState = 1;
        Assert.Equal(
            InventoryContainerPlacementRejection.SourceBeingTraded,
            InventoryContainerPlacementPolicy.Evaluate(objects, 10u, Player, Player));
    }

    [Fact]
    public void FullMessageMatchesRetailContainerTypeBranches()
    {
        var player = new ClientObject { ObjectId = Player, Name = "Backpack" };
        var bag = new ClientObject { ObjectId = 2u, Name = "Pack" };
        Assert.Equal(
            "Backpack is completely full!",
            InventoryContainerPlacementPolicy.ComposeClientLocal(
                InventoryContainerPlacementRejection.ItemCapacityFull,
                null,
                player,
                Player));
        Assert.Equal(
            "The Pack can fit no more containers!",
            InventoryContainerPlacementPolicy.ComposeClientLocal(
                InventoryContainerPlacementRejection.ContainerCapacityFull,
                null,
                bag,
                Player));
    }
}
