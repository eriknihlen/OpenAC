using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class InventoryPlacementSearchTests
{
    private const uint Player = 0x50000001u;
    private const uint MainPackSize = 3;
    private const uint SidePackA = 0x60000010u;
    private const uint SidePackB = 0x60000011u;

    private static ClientObjectTable PlayerWithPacks(int mainCapacity = 3, int containersCapacity = 2)
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Name = "Tester",
            Type = ItemType.Creature,
            ItemsCapacity = mainCapacity,
            ContainersCapacity = containersCapacity,
        });
        AddPack(objects, SidePackA, "Pack A", 2, slot: 0);
        AddPack(objects, SidePackB, "Pack B", 2, slot: 1);
        return objects;
    }

    private static void AddPack(ClientObjectTable objects, uint guid, string name, int capacity, int slot)
    {
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = guid,
            Name = name,
            Type = ItemType.Container,
            ItemsCapacity = capacity,
        });
        objects.MoveItem(guid, Player, slot);
    }

    private static uint AddLoose(ClientObjectTable objects, uint guid, uint container)
    {
        objects.AddOrUpdate(new ClientObject { ObjectId = guid, Name = $"Item {guid:X}", Type = ItemType.Misc });
        objects.MoveItem(guid, container, objects.GetContents(container).Count);
        return guid;
    }

    private static uint WorldItem(ClientObjectTable objects, uint guid = 0x70000001u)
    {
        objects.AddOrUpdate(new ClientObject { ObjectId = guid, Name = "Loot", Type = ItemType.Misc });
        return guid;
    }

    [Fact]
    public void NamedContainerWithRoom_IsUsedAsIs()
    {
        ClientObjectTable objects = PlayerWithPacks();
        uint loot = WorldItem(objects);

        uint chosen = InventoryPlacementSearch.ChooseContainer(
            objects, loot, rootId: Player, targetId: SidePackB, Player, out var refusal);

        Assert.Equal(SidePackB, chosen);
        Assert.Equal(InventoryContainerPlacementRejection.None, refusal);
    }

    [Fact]
    public void FullMainPack_FallsThroughToTheFirstSidePackWithRoom()
    {
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: 1);
        AddLoose(objects, 0x70000100u, Player);          // main pack full (packs do not count)
        AddLoose(objects, 0x70000101u, SidePackA);
        AddLoose(objects, 0x70000102u, SidePackA);       // pack A full
        uint loot = WorldItem(objects);

        uint chosen = InventoryPlacementSearch.ChooseContainer(
            objects, loot, rootId: Player, targetId: Player, Player, out var refusal);

        Assert.Equal(SidePackB, chosen);
        Assert.Equal(InventoryContainerPlacementRejection.None, refusal);
    }

    [Fact]
    public void NoTargetNamed_TriesTheMainPackFirst()
    {
        ClientObjectTable objects = PlayerWithPacks();
        uint loot = WorldItem(objects);

        uint chosen = InventoryPlacementSearch.ChooseContainer(
            objects, loot, rootId: Player, targetId: 0u, Player, out _);

        Assert.Equal(Player, chosen);
    }

    [Fact]
    public void EverythingFull_RefusesWithTheBackpackNotice()
    {
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: 1);
        AddLoose(objects, 0x70000100u, Player);
        AddLoose(objects, 0x70000101u, SidePackA);
        AddLoose(objects, 0x70000102u, SidePackA);
        AddLoose(objects, 0x70000103u, SidePackB);
        AddLoose(objects, 0x70000104u, SidePackB);
        uint loot = WorldItem(objects);

        uint chosen = InventoryPlacementSearch.ChooseContainer(
            objects, loot, rootId: Player, targetId: Player, Player, out var refusal);

        Assert.Equal(0u, chosen);
        Assert.Equal(InventoryContainerPlacementRejection.ItemCapacityFull, refusal);
        Assert.Equal(
            "Backpack is completely full!",
            InventoryContainerPlacementPolicy.ComposeClientLocal(
                refusal, objects.Get(loot), objects.Get(Player), Player));
    }

    [Fact]
    public void APackNeedsAContainerSlot_AndTheNoticeNamesThePlayer()
    {
        ClientObjectTable objects = PlayerWithPacks(containersCapacity: 2); // two side packs already
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x60000020u, Name = "New Pack", Type = ItemType.Container, ItemsCapacity = 4,
        });

        uint chosen = InventoryPlacementSearch.ChooseContainer(
            objects, 0x60000020u, rootId: Player, targetId: Player, Player, out var refusal);

        Assert.Equal(0u, chosen);
        Assert.Equal(InventoryContainerPlacementRejection.ContainerCapacityFull, refusal);
        Assert.Equal(
            $"{objects.Get(Player)!.GetAppropriateName()} can carry no more containers!",
            InventoryContainerPlacementPolicy.ComposeClientLocal(
                refusal, objects.Get(0x60000020u), objects.Get(Player), Player));
    }

    [Fact]
    public void AnItemAlreadyInTheContainer_FitsEvenWhenItIsFull()
    {
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: 1);
        uint inside = AddLoose(objects, 0x70000100u, Player);

        Assert.True(InventoryPlacementSearch.WillItemFitInContainer(objects, inside, Player, Player));
    }

    [Fact]
    public void UnboundedCapacity_AlwaysFits()
    {
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: -1);
        for (uint i = 0; i < 10; i++)
            AddLoose(objects, 0x70000200u + i, Player);
        uint loot = WorldItem(objects);

        Assert.True(InventoryPlacementSearch.WillItemFitInContainer(objects, loot, Player, Player));
    }

    [Fact]
    public void AContainerNotOwnedByThePlayer_NeverFits()
    {
        ClientObjectTable objects = PlayerWithPacks();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x60000030u, Name = "Chest", Type = ItemType.Container, ItemsCapacity = 10,
        });
        uint loot = WorldItem(objects);

        Assert.False(InventoryPlacementSearch.WillItemFitInContainer(objects, loot, 0x60000030u, Player));
    }
}
