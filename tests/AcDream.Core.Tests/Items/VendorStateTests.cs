using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class VendorStateTests
{
    [Fact]
    public void Apply_ZeroGuid_IsANoOp()
    {
        var state = new VendorState();
        var changes = new List<VendorTransition>();
        state.Changed += changes.Add;

        Assert.False(state.Apply(0u, default, Array.Empty<VendorShopItem>()));

        Assert.Equal(0u, state.VendorId);
        Assert.Empty(changes);
    }

    [Fact]
    public void Apply_NewVendor_PublishesOpenedAndStoresSnapshot()
    {
        var state = new VendorState();
        var changes = new List<VendorTransition>();
        state.Changed += changes.Add;

        var profile = new VendorShopProfile(
            MerchandiseItemTypes: (uint)ItemType.MeleeWeapon,
            MerchandiseMinValue: 1,
            MerchandiseMaxValue: 5000,
            DealMagicalItems: true,
            BuyPrice: 0.5f,
            SellPrice: 1.5f,
            AlternateCurrencyWcid: 0,
            AlternateCurrencyAmount: 0,
            AlternateCurrencyPluralName: string.Empty);
        var items = new[]
        {
            new VendorShopItem(0x50000A01u, 3, 42u, "Iron Dagger", (uint)ItemType.MeleeWeapon, 0x06001234u, 25),
        };

        Assert.True(state.Apply(0x40000001u, profile, items));

        Assert.Equal(0x40000001u, state.VendorId);
        Assert.Equal(profile, state.Profile);
        Assert.Same(items, state.Items);

        var change = Assert.Single(changes);
        Assert.Equal(VendorStateTransitionKind.Opened, change.Kind);
        Assert.Equal(0u, change.PreviousVendorId);
        Assert.Equal(0x40000001u, change.VendorId);
    }

    [Fact]
    public void Apply_SameVendorAgain_PublishesRefreshedNotOpened()
    {
        var state = new VendorState();
        state.Apply(0x40000002u, default, Array.Empty<VendorShopItem>());

        var changes = new List<VendorTransition>();
        state.Changed += changes.Add;

        Assert.True(state.Apply(0x40000002u, default, Array.Empty<VendorShopItem>()));

        var change = Assert.Single(changes);
        Assert.Equal(VendorStateTransitionKind.Refreshed, change.Kind);
        Assert.Equal(0x40000002u, change.PreviousVendorId);
        Assert.Equal(0x40000002u, change.VendorId);
    }

    [Fact]
    public void Apply_DifferentVendor_PublishesOpenedWithPreviousId()
    {
        var state = new VendorState();
        state.Apply(0x40000003u, default, Array.Empty<VendorShopItem>());

        var changes = new List<VendorTransition>();
        state.Changed += changes.Add;

        Assert.True(state.Apply(0x40000004u, default, Array.Empty<VendorShopItem>()));

        var change = Assert.Single(changes);
        Assert.Equal(VendorStateTransitionKind.Opened, change.Kind);
        Assert.Equal(0x40000003u, change.PreviousVendorId);
        Assert.Equal(0x40000004u, change.VendorId);
        Assert.Equal(0x40000004u, state.VendorId);
    }

    [Fact]
    public void Close_WithNothingOpen_IsANoOp()
    {
        var state = new VendorState();
        Assert.False(state.Close());
    }

    [Fact]
    public void Close_ClearsSnapshotAndPublishesClosed()
    {
        var state = new VendorState();
        state.Apply(0x40000005u, default, new[]
        {
            new VendorShopItem(0x50000A02u, 1, 7u, "Rock", (uint)ItemType.Misc, 0u, 1),
        });

        var changes = new List<VendorTransition>();
        state.Changed += changes.Add;

        Assert.True(state.Close());

        Assert.Equal(0u, state.VendorId);
        Assert.Equal(default(VendorShopProfile), state.Profile);
        Assert.Empty(state.Items);

        var change = Assert.Single(changes);
        Assert.Equal(VendorStateTransitionKind.Closed, change.Kind);
        Assert.Equal(0x40000005u, change.PreviousVendorId);
        Assert.Equal(0u, change.VendorId);
    }

    [Fact]
    public void Close_ThrowingObserver_DoesNotPropagateAndStillClosesTheSession()
    {
        var state = new VendorState();
        state.Apply(0x40000007u, default, Array.Empty<VendorShopItem>());

        bool secondObserverRan = false;
        state.Changed += _ => throw new InvalidOperationException("boom");
        state.Changed += _ => secondObserverRan = true;

        Exception? thrown = Record.Exception(() => { state.Close(); });

        Assert.Null(thrown);
        Assert.True(secondObserverRan);
        Assert.Equal(0u, state.VendorId);
    }

    [Fact]
    public void Apply_ThrowingObserver_DoesNotPropagateAndDoesNotStarveOtherListeners()
    {
        var state = new VendorState();

        bool secondObserverRan = false;
        state.Changed += _ => throw new InvalidOperationException("boom");
        state.Changed += _ => secondObserverRan = true;

        Exception? thrown = Record.Exception(
            () => state.Apply(0x40000008u, default, Array.Empty<VendorShopItem>()));

        Assert.Null(thrown);
        Assert.True(secondObserverRan);
        Assert.Equal(0x40000008u, state.VendorId);
    }

    [Fact]
    public void Reset_RetryRepublishesAndOneObserverCannotStarveAnother()
    {
        var state = new VendorState();
        state.Apply(0x40000006u, default, Array.Empty<VendorShopItem>());

        bool fail = true;
        int delivered = 0;
        state.Changed += _ =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("transient");
            }
        };
        state.Changed += transition =>
        {
            Assert.Equal(VendorStateTransitionKind.Reset, transition.Kind);
            delivered++;
        };

        Assert.Throws<AggregateException>(() => state.Reset());
        Assert.Equal(1, delivered);
        Assert.Equal(0u, state.VendorId);

        Assert.False(state.Reset());
        Assert.Equal(2, delivered);
    }
}
