using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class VendorShopItemMaterializerTests
{
    private const uint VendorGuid = 0x40001000u;
    private const uint OtherVendorGuid = 0x40002000u;
    private const uint ItemA = 0x50002000u;
    private const uint ItemB = 0x50002001u;

    private static VendorShopItem Item(uint guid, string name = "Item", int? descStackSize = null) =>
        new(guid, StackSize: -1, WeenieClassId: 1u, Name: name, ItemType: (uint)ItemType.Misc,
            IconId: 0x1234u, Value: 10, DescStackSize: descStackSize);

    [Fact]
    public void Apply_MaterializesEachShopItemWithVendorAsContainer()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "Iron Sword"), Item(ItemB, "Bread") });

        ClientObject? a = objects.Get(ItemA);
        ClientObject? b = objects.Get(ItemB);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(VendorGuid, a!.ContainerId);
        Assert.Equal(VendorGuid, b!.ContainerId);
        Assert.Equal("Iron Sword", a.Name);
        Assert.Equal(2, materializer.OwnedCount);
        Assert.True(materializer.Owns(ItemA));
        Assert.True(materializer.Owns(ItemB));
    }

    [Fact]
    public void Close_RemovesEveryMaterializedItem()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA), Item(ItemB) });

        vendor.Close();

        Assert.Null(objects.Get(ItemA));
        Assert.Null(objects.Get(ItemB));
        Assert.Equal(0, materializer.OwnedCount);
    }

    [Fact]
    public void Reset_RemovesEveryMaterializedItem()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA) });

        vendor.Reset();

        Assert.Null(objects.Get(ItemA));
        Assert.Equal(0, materializer.OwnedCount);
    }

    [Fact]
    public void DifferentVendorSupersedes_RemovesPriorVendorsItemsBeforeMaterializingTheNewOnes()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "First Vendor Item") });
        Assert.NotNull(objects.Get(ItemA));

        const uint NewItem = 0x50003000u;
        vendor.Apply(OtherVendorGuid, default, new[] { Item(NewItem, "Second Vendor Item") });

        Assert.Null(objects.Get(ItemA));
        ClientObject? replacement = objects.Get(NewItem);
        Assert.NotNull(replacement);
        Assert.Equal(OtherVendorGuid, replacement!.ContainerId);
        Assert.Equal(1, materializer.OwnedCount);
    }

    [Fact]
    public void Refreshed_SameVendor_DoesNotFireObjectRemovedForStillListedItems()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "Chainmail") });

        var removed = new List<uint>();
        objects.ObjectRemoved += o => removed.Add(o.ObjectId);

        // Same vendor id re-approaches with the SAME item guid still listed
        // (e.g. a post-buy refresh where this item wasn't the one bought)
        // but with a refreshed field value.
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "Chainmail", descStackSize: 5) });

        Assert.Empty(removed);
        Assert.Equal(1, materializer.OwnedCount);
        Assert.Equal(5, objects.Get(ItemA)!.StackSize);
    }

    [Fact]
    public void Apply_UnlimitedStockNoDescStackSize_FallsBackToMaxStackSize()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        vendor.Apply(VendorGuid, default, new[]
        {
            new VendorShopItem(
                ItemA, StackSize: -1, WeenieClassId: 1u, Name: "Prismatic Taper",
                ItemType: (uint)ItemType.SpellComponents, IconId: 0x1234u, Value: 100,
                DescStackSize: null, MaxStackSize: 1000),
        });

        ClientObject item = objects.Get(ItemA)!;
        Assert.Equal(1000, item.StackSize);
        Assert.Equal(1000, item.StackSizeMax);
    }

    [Fact]
    public void Apply_LiveAceWireShape_DescOneMaxHundred_ResolvesToTheAuthoredCeiling()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        vendor.Apply(VendorGuid, default, new[]
        {
            new VendorShopItem(
                ItemA, StackSize: -1, WeenieClassId: 1u, Name: "Lead Scarab",
                ItemType: (uint)ItemType.SpellComponents, IconId: 0x1234u, Value: 10,
                DescStackSize: 1, MaxStackSize: 100),
        });

        ClientObject item = objects.Get(ItemA)!;
        Assert.Equal(100, item.StackSize);
        Assert.Equal(100, item.StackSizeMax);
    }

    [Fact]
    public void Apply_UnlimitedSupplySentinel_FallsBackToNonSplittableDefault()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "Bread") }); // StackSize: -1, DescStackSize: null, MaxStackSize: null

        Assert.Equal(1, objects.Get(ItemA)!.StackSize);
    }

    [Fact]
    public void Refreshed_ItemNoLongerListed_IsRemoved()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA), Item(ItemB) });

        vendor.Apply(VendorGuid, default, new[] { Item(ItemB) });

        Assert.Null(objects.Get(ItemA));
        Assert.NotNull(objects.Get(ItemB));
        Assert.Equal(1, materializer.OwnedCount);
    }

    [Fact]
    public void Refreshed_ItemPurchased_ReparentedIntoBuyerPack_Survives()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "Unique Sword"), Item(ItemB) });
        Assert.Equal(VendorGuid, objects.Get(ItemA)!.ContainerId);

        const uint BuyerGuid = 0x50009000u;
        objects.Ingest(new WeenieData(
            Guid: ItemA,
            Name: null,
            Type: null,
            WeenieClassId: 0,
            IconId: 0,
            IconOverlayId: 0,
            IconUnderlayId: 0,
            Effects: 0,
            Value: null,
            StackSize: null,
            StackSizeMax: null,
            Burden: null,
            ContainerId: BuyerGuid,
            WielderId: null,
            ValidLocations: null,
            CurrentWieldedLocation: null,
            Priority: null,
            ItemsCapacity: null,
            ContainersCapacity: null,
            Structure: null,
            MaxStructure: null,
            Workmanship: null));
        Assert.Equal(BuyerGuid, objects.Get(ItemA)!.ContainerId);

        // Post-buy ApproachVendor refresh: the purchased item is gone from
        // the shop's own list.
        vendor.Apply(VendorGuid, default, new[] { Item(ItemB) });

        ClientObject? survivor = objects.Get(ItemA);
        Assert.NotNull(survivor);
        Assert.Equal(BuyerGuid, survivor!.ContainerId);
        Assert.False(materializer.Owns(ItemA));
        Assert.Equal(1, materializer.OwnedCount);
    }

    [Fact]
    public void CollidingGuid_AlreadyOwnedBySomethingElse_IsNeverClobbered()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        // Simulate a pre-existing, non-vendor-owned object at this guid --
        // e.g. a live entity, or an item still sitting in someone's
        // inventory/equipment.
        const uint LiveOwner = 0x60000001u;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ItemA,
            Name = "Definitely Not A Shop Item",
            ContainerId = LiveOwner,
        });
        using var materializer = new VendorShopItemMaterializer(vendor, objects);

        vendor.Apply(VendorGuid, default, new[] { Item(ItemA, "Shop Listing With A Colliding Guid") });

        ClientObject? survivor = objects.Get(ItemA);
        Assert.NotNull(survivor);
        Assert.Equal("Definitely Not A Shop Item", survivor!.Name);
        Assert.Equal(LiveOwner, survivor.ContainerId);
        Assert.False(materializer.Owns(ItemA));
        Assert.Equal(0, materializer.OwnedCount);

        vendor.Close();
        Assert.NotNull(objects.Get(ItemA));
    }

    [Fact]
    public void Dispose_UnsubscribesFromVendorChanged()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA) });
        Assert.NotNull(objects.Get(ItemA));

        materializer.Dispose();

        vendor.Close();
        Assert.NotNull(objects.Get(ItemA));
    }

    [Fact]
    public void Retire_ThrowingObjectRemovedObserver_StillRetiresRemainingGuidsAndConverges()
    {
        var vendor = new VendorState();
        var objects = new ClientObjectTable();
        using var materializer = new VendorShopItemMaterializer(vendor, objects);
        vendor.Apply(VendorGuid, default, new[] { Item(ItemA), Item(ItemB) });
        Assert.Equal(2, materializer.OwnedCount);

        objects.ObjectRemoved += _ => throw new InvalidOperationException("boom");

        vendor.Close();

        Assert.Null(objects.Get(ItemA));
        Assert.Null(objects.Get(ItemB));
        Assert.Equal(0, materializer.OwnedCount);
        Assert.False(materializer.Owns(ItemA));
        Assert.False(materializer.Owns(ItemB));

        materializer.Dispose();
        Assert.Equal(0, materializer.OwnedCount);
    }
}
