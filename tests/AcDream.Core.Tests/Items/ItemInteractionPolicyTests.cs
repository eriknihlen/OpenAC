using AcDream.Core.Items;

namespace AcDream.Core.Tests.Items;

public sealed class ItemInteractionPolicyTests
{
    private const uint Player = 0x50000001u;
    private const uint GroundContainer = 0x70000001u;

    [Fact]
    public void ToolbarUseEnabled_matchesRetailSelectionPredicate()
    {
        Assert.True(ItemInteractionPolicy.IsToolbarUseEnabled(
            ItemType.MeleeWeapon,
            combatUse: 1,
            useability: ItemUseability.No));
        Assert.True(ItemInteractionPolicy.IsToolbarUseEnabled(
            ItemType.Armor | ItemType.Clothing,
            combatUse: 0,
            useability: ItemUseability.No));
        Assert.True(ItemInteractionPolicy.IsToolbarUseEnabled(
            ItemType.Misc,
            combatUse: 0,
            useability: 0x00220008u));

        Assert.False(ItemInteractionPolicy.IsToolbarUseEnabled(
            ItemType.SpellComponents,
            combatUse: 0,
            useability: ItemUseability.No));
    }

    [Fact]
    public void DetermineUseResult_looseItemAndComponentPack_matchRetailPickupBranch()
    {
        Assert.Equal(ItemPrimaryUseResult.PlaceInBackpack,
            Determine(Obj(0x6001)));

        var requiresSlot = Obj(0x6002) with
        {
            Flags = PublicWeenieFlags.RequiresPackSlot,
            ItemsCapacity = 12,
        };
        Assert.Equal(ItemPrimaryUseResult.None, Determine(requiresSlot));
        Assert.Equal(ItemPrimaryUseResult.PlaceInBackpack,
            Determine(requiresSlot with { IsComponentPack = true }));
    }

    [Fact]
    public void DetermineUseResult_onlyAllowsContainedPickupFromViewedGroundContainer()
    {
        var item = Obj(0x6001) with { ContainerId = GroundContainer };

        Assert.Equal(ItemPrimaryUseResult.None,
            ItemInteractionPolicy.DetermineUseResult(item, Player, 0));
        Assert.Equal(ItemPrimaryUseResult.PlaceInBackpack,
            ItemInteractionPolicy.DetermineUseResult(item, Player, GroundContainer));
    }

    [Fact]
    public void DetermineUseResult_portsAllOwnedPrimaryClassifications()
    {
        var owned = Obj(0x6001) with
        {
            ContainerId = Player,
            OwnedByPlayer = true,
            Flags = PublicWeenieFlags.RequiresPackSlot,
        };

        Assert.Equal(ItemPrimaryUseResult.WieldRight,
            Determine(owned with { CombatUse = 1 }));
        Assert.Equal(ItemPrimaryUseResult.WieldLeft,
            Determine(owned with { CombatUse = 1, Flags = owned.Flags | PublicWeenieFlags.WieldLeft }));
        Assert.Equal(ItemPrimaryUseResult.AutoSort,
            Determine(owned with { ValidLocations = EquipMask.HeadWear }));
        Assert.Equal(ItemPrimaryUseResult.OpenSalvage,
            Determine(owned with { Type = ItemType.TinkeringTool }));
        Assert.Equal(ItemPrimaryUseResult.ItemUse,
            Determine(owned with { Useability = ItemUseability.Contained }));
    }

    [Fact]
    public void DetermineUseResult_zeroValuedOwnedGem_isRetailItemUse()
    {
        var favor = Obj(0x6002) with
        {
            Type = ItemType.Gem,
            ContainerId = Player,
            OwnedByPlayer = true,
            Flags = PublicWeenieFlags.RequiresPackSlot,
            Useability = ItemUseability.Undef,
        };

        Assert.Equal(ItemPrimaryUseResult.ItemUse, Determine(favor));

        var decision = ItemInteractionPolicy.DecideUse(Use(favor));
        Assert.Equal(
            new[]
            {
                ItemPolicyActionKind.SendUse,
                ItemPolicyActionKind.IncrementBusy,
            },
            decision.Actions.Select(action => action.Kind));
    }

    [Fact]
    public void DetermineUseResult_nonOwnedGameAndPlayerPaths_areDistinct()
    {
        Assert.Equal(ItemPrimaryUseResult.BeginGame,
            Determine(Obj(0x6001) with
            {
                Type = ItemType.Gameboard,
                Flags = PublicWeenieFlags.RequiresPackSlot,
            }));
        Assert.Equal(ItemPrimaryUseResult.OpenSecureTrade,
            Determine(Obj(0x6002) with
            {
                Flags = PublicWeenieFlags.Player | PublicWeenieFlags.RequiresPackSlot,
            }));
    }

    [Fact]
    public void UseObject_excludesResultEightFromEarlyClassifiedBound_thenStillWieldsLeft()
    {
        var source = Obj(0x6001) with
        {
            ContainerId = Player,
            OwnedByPlayer = true,
            CombatUse = 1,
            Flags = PublicWeenieFlags.RequiresPackSlot | PublicWeenieFlags.WieldLeft,
        };

        var decision = ItemInteractionPolicy.DecideUse(Use(source));

        Assert.Equal(new[] { ItemPolicyActionKind.WieldLeft },
            decision.Actions.Select(a => a.Kind));
    }

    [Fact]
    public void UseObject_readyVendorTradeAndWieldedSourceGates_areOrdered()
    {
        var direct = OwnedDirect();

        Assert.Empty(ItemInteractionPolicy.DecideUse(
            Use(direct) with { ReadyForInventoryRequest = false }).Actions);
        Assert.Empty(ItemInteractionPolicy.DecideUse(
            Use(direct) with { ActiveVendorId = 0x7001, Source = direct with { ContainerId = 0x7001 } }).Actions);
        Assert.Equal(ItemPolicyActionKind.Reject,
            Assert.Single(ItemInteractionPolicy.DecideUse(
                Use(direct with { TradeState = 1 })).Actions).Kind);
        Assert.Equal("You cannot use the item because you are trading it",
            Assert.Single(ItemInteractionPolicy.DecideUse(
                Use(direct with { TradeState = 1 })).Actions).Message);
        Assert.Equal("You must wield the item to use it",
            Assert.Single(ItemInteractionPolicy.DecideUse(
                Use(direct with { Useability = ItemUseability.Wielded })).Actions).Message);
    }

    [Fact]
    public void UseObject_targetCompatibilityFailures_useRetailVerbatimMessages()
    {
        var source = OwnedDirect() with
        {
            Name = "mana stone",
            Useability = 0x00080008u,
            TargetType = (uint)ItemType.Misc,
        };
        var target = Obj(0x6002) with
        {
            Name = "armor",
            Type = ItemType.Armor,
            ContainerId = Player,
            OwnedByPlayer = true,
        };

        var missing = ItemInteractionPolicy.DecideUse(Use(source) with
        {
            UseCurrentSelection = true,
        });
        Assert.Equal("Select your target before using the mana stone",
            Assert.Single(missing.Actions).Message);

        var incompatible = ItemInteractionPolicy.DecideUse(Use(source) with
        {
            UseCurrentSelection = true,
            SelectedTarget = target,
        });
        Assert.Equal("Cannot use the mana stone with the armor",
            Assert.Single(incompatible.Actions).Message);

        var traded = ItemInteractionPolicy.DecideUse(Use(source) with
        {
            UseCurrentSelection = true,
            SelectedTarget = target with { TradeState = 1 },
        });
        Assert.Equal("You can't use the mana stone on an item you are trading",
            Assert.Single(traded.Actions).Message);
    }

    [Fact]
    public void UseObject_targetedUse_appliesKindAndLocationGate_thenSendsAndMarksBusy()
    {
        var source = OwnedDirect() with
        {
            Useability = 0x00080008u,
            TargetType = (uint)ItemType.Misc,
        };
        var target = Obj(0x6002) with
        {
            ContainerId = Player,
            OwnedByPlayer = true,
            Type = ItemType.Misc,
        };

        var decision = ItemInteractionPolicy.DecideUse(Use(source) with
        {
            UseCurrentSelection = true,
            SelectedTarget = target,
        });

        Assert.Equal(new[]
        {
            ItemPolicyActionKind.SendUseWithTarget,
            ItemPolicyActionKind.IncrementBusy,
        }, decision.Actions.Select(a => a.Kind));
    }

    [Theory]
    [InlineData(PublicWeenieFlags.PlayerKillerSwitch, ItemPolicyActionKind.ConfirmPlayerKillerSwitch)]
    [InlineData(PublicWeenieFlags.NonPlayerKillerSwitch, ItemPolicyActionKind.ConfirmNonPlayerKillerSwitch)]
    [InlineData(PublicWeenieFlags.VolatileRare, ItemPolicyActionKind.ConfirmVolatileRare)]
    public void UseObject_confirmationFlags_blockWireUse(
        PublicWeenieFlags flag,
        ItemPolicyActionKind expected)
    {
        var decision = ItemInteractionPolicy.DecideUse(
            Use(OwnedDirect() with { Flags = flag | PublicWeenieFlags.RequiresPackSlot }));

        Assert.Equal(expected, Assert.Single(decision.Actions).Kind);
    }

    [Fact]
    public void Placement_vendorAndSecureTrade_actButPreserveRetailFalseReturn()
    {
        var item = OwnedDirect() with { StackSize = 10, MaxSplitSize = 10 };
        var vendor = Obj(0x7001) with { Flags = PublicWeenieFlags.Vendor };
        var sale = ItemInteractionPolicy.DecidePlacement(Place(item, vendor));
        Assert.False(sale.ReturnValue);
        Assert.Equal(ItemPolicyActionKind.SellToVendor, Assert.Single(sale.Actions).Kind);

        var player = Obj(0x7002) with { Flags = PublicWeenieFlags.Player };
        var trade = ItemInteractionPolicy.DecidePlacement(Place(item, player));
        Assert.False(trade.ReturnValue);
        Assert.Equal(ItemPolicyActionKind.StartSecureTrade, Assert.Single(trade.Actions).Kind);
    }

    [Fact]
    public void Placement_groundStackAndAirborneMatrix_matchesRetail()
    {
        var item = OwnedDirect() with { StackSize = 10, MaxSplitSize = 10 };

        var airborne = ItemInteractionPolicy.DecidePlacement(PlaceOnGround(item) with
        {
            PlayerOnGround = false,
        });
        Assert.False(airborne.ReturnValue);
        Assert.Equal("You cannot do that in mid air", Assert.Single(airborne.Actions).Message);

        var split = ItemInteractionPolicy.DecidePlacement(PlaceOnGround(item) with { SplitSize = 4 });
        Assert.True(split.ReturnValue);
        Assert.Equal(ItemPolicyActionKind.SplitToWorld, Assert.Single(split.Actions).Kind);

        var full = ItemInteractionPolicy.DecidePlacement(PlaceOnGround(item));
        Assert.True(full.ReturnValue);
        Assert.Equal(ItemPolicyActionKind.DropToWorld, Assert.Single(full.Actions).Kind);

        var alreadyWorld = ItemInteractionPolicy.DecidePlacement(
            PlaceOnGround(item with { IsIn3DView = true }));
        Assert.False(alreadyWorld.ReturnValue);
        Assert.Equal("Move cancelled", Assert.Single(alreadyWorld.Actions).Message);
    }

    [Fact]
    public void Placement_containerRequiresOpenableAndCurrentGroundObject()
    {
        var item = OwnedDirect();
        var container = Obj(GroundContainer) with { IsContainer = true };

        var locked = ItemInteractionPolicy.DecidePlacement(Place(item, container) with
        {
            GroundObjectId = GroundContainer,
        });
        Assert.False(locked.ReturnValue);

        var open = container with { Flags = PublicWeenieFlags.Openable };
        var closedView = ItemInteractionPolicy.DecidePlacement(Place(item, open));
        Assert.False(closedView.ReturnValue);

        var placed = ItemInteractionPolicy.DecidePlacement(Place(item, open) with
        {
            GroundObjectId = GroundContainer,
        });
        Assert.True(placed.ReturnValue);
        Assert.Equal(ItemPolicyActionKind.PlaceInContainer, Assert.Single(placed.Actions).Kind);
    }

    private static ItemPrimaryUseResult Determine(ItemPolicyObject item)
        => ItemInteractionPolicy.DetermineUseResult(item, Player, 0);

    private static ItemPolicyObject OwnedDirect()
        => Obj(0x6001) with
        {
            ContainerId = Player,
            OwnedByPlayer = true,
            Flags = PublicWeenieFlags.RequiresPackSlot,
            Useability = ItemUseability.Contained,
        };

    private static ItemPolicyObject Obj(uint id) => new(
        id,
        ItemType.Misc,
        PublicWeenieFlags.None,
        ContainerId: 0,
        WielderId: 0,
        ValidLocations: EquipMask.None,
        CurrentLocation: EquipMask.None,
        CombatUse: 0,
        ItemsCapacity: 0,
        ContainersCapacity: 0,
        Useability: ItemUseability.No,
        TargetType: 0,
        OwnedByPlayer: false,
        IsContainer: false,
        IsComponentPack: false,
        TradeState: 0,
        StackSize: 1,
        MaxSplitSize: 1,
        IsIn3DView: false);

    private static ItemUsePolicyInput Use(ItemPolicyObject source) => new(
        source,
        Player,
        GroundObjectId: 0,
        ReadyForInventoryRequest: true,
        ActiveVendorId: 0,
        BypassClassification: false,
        UseCurrentSelection: false,
        SelectedTarget: null,
        ConfirmVolatileRareUses: true,
        InNonCombatMode: false);

    private static ItemPlacementPolicyInput Place(
        ItemPolicyObject item,
        ItemPolicyObject target) => new(
        item,
        Player,
        GroundObjectId: 0,
        ReadyForInventoryRequest: true,
        TargetId: target.Id,
        Target: target,
        AllowGroundFallback: false,
        MergeAccepted: false,
        DragOnPlayerOpensSecureTrade: true,
        PlayerOnGround: true,
        SplitSize: item.StackSize);

    private static ItemPlacementPolicyInput PlaceOnGround(ItemPolicyObject item) => new(
        item,
        Player,
        GroundObjectId: 0,
        ReadyForInventoryRequest: true,
        TargetId: 0,
        Target: null,
        AllowGroundFallback: true,
        MergeAccepted: false,
        DragOnPlayerOpensSecureTrade: true,
        PlayerOnGround: true,
        SplitSize: item.StackSize);
}
