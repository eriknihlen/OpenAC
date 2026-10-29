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

    /// <summary>
    /// A spell focus carries the container-type field a pack carries, and
    /// it takes a container slot rather than an item slot, so the slot
    /// accounting counts it as a container. It holds nothing, though, so it
    /// is never somewhere an item can go: a request naming one as the
    /// destination is refused by the server, and every pull that picks it
    /// is lost. Mutation: drop the focus guard from
    /// <c>WillItemFitInContainer</c> and the focus is chosen over the
    /// "everything is full" refusal.
    /// </summary>
    [Fact]
    public void AFocusIsNeverChosenAsAPlacementDestination()
    {
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: 1);
        AddLoose(objects, 0x70000100u, Player);          // main pack full
        AddLoose(objects, 0x70000101u, SidePackA);
        AddLoose(objects, 0x70000102u, SidePackA);       // pack A full
        AddLoose(objects, 0x70000103u, SidePackB);
        AddLoose(objects, 0x70000104u, SidePackB);       // pack B full
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x60000020u,
            Name = "Foci of Verdancy",
            Type = ItemType.Misc,
            ContainerTypeHint = 2u,
        });
        objects.MoveItem(0x60000020u, Player, 2);
        uint loot = WorldItem(objects);

        uint chosen = InventoryPlacementSearch.ChooseContainer(
            objects, loot, rootId: Player, targetId: Player, Player, out var refusal);

        Assert.Equal(0u, chosen);
        Assert.Equal(InventoryContainerPlacementRejection.ItemCapacityFull, refusal);
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
    public void PartOfAStackAlreadyInAFullPack_NeedsASlotOfItsOwn()
    {
        // Only the whole stack counts as already fitting where it is.
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: 1);
        uint inside = AddLoose(objects, 0x70000100u, Player);

        Assert.True(InventoryPlacementSearch.WillItemFitInContainer(objects, inside, Player, Player, wholeStack: true));
        Assert.False(InventoryPlacementSearch.WillItemFitInContainer(objects, inside, Player, Player, wholeStack: false));
        Assert.Equal(InventoryContainerPlacementRejection.None,
            InventoryContainerPlacementPolicy.Evaluate(objects, inside, Player, Player, wholeStack: true));
        Assert.Equal(InventoryContainerPlacementRejection.ItemCapacityFull,
            InventoryContainerPlacementPolicy.Evaluate(objects, inside, Player, Player, wholeStack: false));
        Assert.Equal(SidePackA, InventoryPlacementSearch.ChooseContainer(
            objects, inside, rootId: Player, targetId: Player, Player, out _, wholeStack: false));
    }

    [Fact]
    public void AnItemCapacityOfZero_HasNoRoom()
    {
        // Only -1 is unbounded; a pack that carries no item slots is passed
        // over by the search.
        ClientObjectTable objects = PlayerWithPacks(mainCapacity: 1);
        AddLoose(objects, 0x70000100u, Player);
        AddPack(objects, SidePackA, "Pack A", 0, slot: 0);
        uint loot = WorldItem(objects);

        Assert.False(InventoryPlacementSearch.WillItemFitInContainer(objects, loot, SidePackA, Player));
        Assert.Equal(SidePackB, InventoryPlacementSearch.ChooseContainer(
            objects, loot, rootId: Player, targetId: Player, Player, out _));
    }

    [Fact]
    public void TheLegalityCheck_ReadsAnItemCapacityOfZeroAsNoRoom()
    {
        ClientObjectTable objects = PlayerWithPacks();
        AddPack(objects, SidePackA, "Pack A", 0, slot: 0);
        uint loot = WorldItem(objects);

        Assert.Equal(InventoryContainerPlacementRejection.ItemCapacityFull,
            InventoryContainerPlacementPolicy.Evaluate(objects, loot, SidePackA, Player));

        // -1 is unbounded; an undescribed player takes anything.
        AddPack(objects, SidePackA, "Pack A", -1, slot: 0);
        Assert.Equal(InventoryContainerPlacementRejection.None,
            InventoryContainerPlacementPolicy.Evaluate(objects, loot, SidePackA, Player));
        var lone = new ClientObjectTable();
        uint alone = WorldItem(lone);
        Assert.Equal(InventoryContainerPlacementRejection.None,
            InventoryContainerPlacementPolicy.Evaluate(lone, alone, Player, Player));
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
