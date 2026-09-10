using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.UI;

public sealed class ItemInteractionControllerTests
{
    private const uint Player = 0x50000001u;
    private const uint Pack = 0x50000010u;
    private const uint HealthKitUseability = 0x00220008u;

    private sealed class Harness
    {
        public readonly ClientObjectTable Objects = new();
        public readonly List<uint> Uses = new();
        public readonly List<uint> Examines = new();
        public readonly List<(uint Source, uint Target)> UseWithTarget = new();
        public readonly List<(uint Item, uint Mask)> Wields = new();
        public readonly List<(uint Item, uint Container, int Placement)> Puts = new();
        public readonly List<(uint Item, uint Container, uint Placement, uint Amount)> SplitPuts = new();
        public readonly List<(uint Source, uint Target, uint Amount)> Merges = new();
        public readonly List<uint> ExternalRequests = new();
        public readonly List<(uint Item, uint Container, int Placement)> BackpackPlacements = new();
        public readonly List<uint> Drops = new();
        public readonly List<(uint Item, uint Amount)> SplitDrops = new();
        public readonly List<(uint Target, uint Item, uint Amount)> Gives = new();
        public readonly List<(uint VendorGuid, uint ItemGuid, int Amount, uint AlternateCurrencyId)> Buys = new();
        public bool SendBuySucceeds = true;
        public readonly List<(uint VendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> Items, uint AlternateCurrencyId)> BuyAlls = new();
        public bool SendBuyAllSucceeds = true;
        public readonly List<(uint VendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> Items)> Sells = new();
        public readonly List<(uint ToolGuid, IReadOnlyList<uint> ItemGuids)> Salvages = new();
        public bool SendSellSucceeds = true;
        public readonly List<string> Toasts = new();
        public readonly List<string> SystemMessages = new();
        public readonly List<(string Text, RetailLogTextType Type)> InterfaceTexts = new();
        public readonly List<CombatMode> CombatModeRequests = new();
        public readonly CombatState Combat = new();
        public readonly StackSplitQuantityState SplitQuantity = new();
        public readonly InventoryTransactionState SharedTransactions;
        public readonly RuntimeInteractionTransactionState RuntimeTransactions;
        public uint SelectedObject;
        public bool NonCombatMode;
        public bool DragOnPlayerOpensSecureTrade = true;
        public uint GroundObject;
        public long Now = 1_000;

        public Harness(
            Action<uint, ItemUseRequestReservation>? requestUse = null)
        {
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Name = "Player",
                Type = ItemType.Creature,
            });
            Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Pack,
                Name = "Backpack",
                Type = ItemType.Container,
                ItemsCapacity = 24,
            });
            Objects.MoveItem(Pack, Player, 0);
            SharedTransactions = new InventoryTransactionState(Objects);
            RuntimeTransactions = new RuntimeInteractionTransactionState(
                SharedTransactions);

            Controller = new ItemInteractionController(
                Objects,
                RuntimeTransactions,
                new InteractionState(),
                playerGuid: () => Player,
                sendUse: requestUse is null ? Uses.Add : null,
                sendExamine: Examines.Add,
                sendUseWithTarget: (source, target) => UseWithTarget.Add((source, target)),
                sendWield: (item, mask) => Wields.Add((item, mask)),
                sendDrop: Drops.Add,
                nowMs: () => Now,
                toast: Toasts.Add,
                sendSplitToWorld: (item, amount) => SplitDrops.Add((item, amount)),
                selectedObjectId: () => SelectedObject,
                stackSplitQuantity: SplitQuantity,
                inNonCombatMode: () => NonCombatMode,
                groundObjectId: () => GroundObject,
                placeInBackpack: (item, container, placement) =>
                    BackpackPlacements.Add((item, container, placement)),
                sendPutItemInContainer: (item, container, placement) =>
                    Puts.Add((item, container, placement)),
                sendGive: (target, item, amount) => Gives.Add((target, item, amount)),
                dragOnPlayerOpensSecureTrade: () => DragOnPlayerOpensSecureTrade,
                systemMessage: SystemMessages.Add,
                sendSplitToContainer: (item, container, placement, amount) =>
                    SplitPuts.Add((item, container, placement, amount)),
                requestExternalContainer: id =>
                {
                    GroundObject = id;
                    ExternalRequests.Add(id);
                },
                combatState: Combat,
                sendChangeCombatMode: CombatModeRequests.Add,
                requestUse: requestUse,
                sendBuy: (vendorGuid, itemGuid, amount, alternateCurrencyId) =>
                {
                    if (!SendBuySucceeds)
                        return false;
                    Buys.Add((vendorGuid, itemGuid, amount, alternateCurrencyId));
                    return true;
                },
                sendBuyAll: (vendorGuid, items, alternateCurrencyId) =>
                {
                    if (!SendBuyAllSucceeds)
                        return false;
                    BuyAlls.Add((vendorGuid, items, alternateCurrencyId));
                    return true;
                },
                sendSell: (vendorGuid, items) =>
                {
                    if (!SendSellSucceeds)
                        return false;
                    Sells.Add((vendorGuid, items));
                    return true;
                },
                interfaceText: (text, type) => InterfaceTexts.Add((text, type)),
                sendStackableMerge: (source, target, amount) =>
                    Merges.Add((source, target, amount)),
                sendSalvage: (tool, items) =>
                {
                    Salvages.Add((tool, items.ToArray()));
                    return true;
                });
        }

        public ItemInteractionController Controller { get; }

        public ClientObject AddContained(uint id, Action<ClientObject>? configure = null)
        {
            var item = new ClientObject
            {
                ObjectId = id,
                Name = $"Item {id:X}",
                Type = ItemType.Misc,
                IconId = 0x06001234u,
            };
            configure?.Invoke(item);
            Objects.AddOrUpdate(item);
            Objects.MoveItem(id, Pack, Objects.GetContents(Pack).Count);
            return item;
        }
    }

    [Fact]
    public void TargetedItem_entersTargetModeAndMarksPendingSource()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item => item.Useability = HealthKitUseability);

        Assert.True(h.Controller.ActivateItem(0x50000A01u));

        Assert.True(h.Controller.IsTargetModeActive);
        Assert.True(h.Controller.IsPendingSource(0x50000A01u));
        Assert.Equal(InteractionModeKind.UseItemOnTarget,
            h.Controller.InteractionState.Current.Kind);
        Assert.Equal(0x50000A01u,
            h.Controller.InteractionState.Current.SourceObjectId);
        Assert.Empty(h.UseWithTarget);
    }

    [Fact]
    public void AutomationApply_dispatchesDirectlyWithoutInstallingTargetMode()
    {
        var h = new Harness();
        const uint source = 0x50000A21u;
        h.AddContained(source, item =>
        {
            item.Useability = HealthKitUseability;
            item.TargetType = (uint)ItemType.Creature;
        });

        Assert.True(h.Controller.TryApplyItem(source, Player));

        Assert.Equal(new[] { (source, Player) }, h.UseWithTarget);
        Assert.False(h.Controller.IsAnyTargetModeActive);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void AutomationApply_reportsRefusalWhenTargetIsIncompatible()
    {
        var h = new Harness();
        const uint source = 0x50000A21u;
        const uint coat = 0x50000A22u;
        h.AddContained(source, item =>
        {
            item.Useability = HealthKitUseability;
            item.TargetType = (uint)ItemType.Creature;
        });
        h.AddContained(coat, item => item.Type = ItemType.Armor);

        Assert.False(h.Controller.TryApplyItem(source, coat));

        Assert.Empty(h.UseWithTarget);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void AutomationUse_dispatchesOnlyAnOrdinaryWireUse()
    {
        var h = new Harness();
        const uint item = 0x50000A23u;
        h.AddContained(item, candidate =>
            candidate.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.TryUseItemForAutomation(item));

        Assert.Equal(new[] { item }, h.Uses);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void AutomationUse_refusesTargetedItemInsteadOfOpeningModalCursor()
    {
        var h = new Harness();
        const uint item = 0x50000A24u;
        h.AddContained(item, candidate =>
            candidate.Useability = HealthKitUseability);

        Assert.False(h.Controller.TryUseItemForAutomation(item));

        Assert.Empty(h.Uses);
        Assert.False(h.Controller.IsAnyTargetModeActive);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void AutomationAppraisalUsesCanonicalOwnerWithoutChangingSelectionMode()
    {
        var h = new Harness();
        const uint item = 0x50000A25u;
        h.AddContained(item);

        Assert.True(h.Controller.TryAppraiseForAutomation(item));

        Assert.Equal(new[] { item }, h.Examines);
        Assert.False(h.Controller.IsAnyTargetModeActive);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void ResetSession_RetryNotifiesEveryStateObserver()
    {
        var h = new Harness();
        h.Controller.InteractionState.EnterExamine();
        h.Controller.IncrementBusyCount();
        bool fail = true;
        int delivered = 0;
        h.Controller.StateChanged += () =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("transient");
            }
        };
        h.Controller.StateChanged += () => delivered++;

        Assert.Throws<AggregateException>(h.Controller.ResetSession);
        Assert.Equal(1, delivered);
        Assert.Equal(0, h.Controller.BusyCount);
        Assert.Equal(InteractionMode.None, h.Controller.InteractionState.Current);

        h.Controller.ResetSession();
        Assert.Equal(2, delivered);
    }

    [Fact]
    public void ExternalContainerUse_RequestsGroundObjectAndSendsUse()
    {
        var h = new Harness();
        const uint chest = 0x70000001u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = chest,
            Type = ItemType.Container,
            ItemsCapacity = 24,
            Useability = ItemUseability.Remote,
        });

        Assert.True(h.Controller.ActivateItem(chest));

        Assert.Equal(new uint[] { chest }, h.Uses);
        Assert.Equal(new uint[] { chest }, h.ExternalRequests);
        Assert.Equal(chest, h.GroundObject);
    }

    [Fact]
    public void CorpseUse_UsesStuckContainerInsteadOfTryingToPickItUp()
    {
        var h = new Harness();
        const uint corpse = 0x70000002u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = corpse,
            Type = ItemType.Container,
            ItemsCapacity = 24,
            Useability = ItemUseability.Remote,
            PublicWeenieBitfield = (uint)(
                PublicWeenieFlags.Openable
                | PublicWeenieFlags.Stuck
                | PublicWeenieFlags.Corpse),
        });

        Assert.True(h.Controller.UseSelectedOrEnterMode(corpse));

        Assert.Equal(new uint[] { corpse }, h.Uses);
        Assert.Equal(new uint[] { corpse }, h.ExternalRequests);
        Assert.Empty(h.Puts);
    }

    [Fact]
    public void DoubleClickItemInOpenCorpse_placesThatItemInBackpack()
    {
        var h = new Harness();
        const uint corpse = 0x70000012u;
        const uint loot = 0x70000013u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = corpse,
            Type = ItemType.Container,
            ItemsCapacity = 24,
            PublicWeenieBitfield = (uint)(
                PublicWeenieFlags.Openable
                | PublicWeenieFlags.Stuck
                | PublicWeenieFlags.Corpse),
        });
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = loot,
            Name = "Loot",
            Type = ItemType.Misc,
        });
        h.Objects.MoveItem(loot, corpse, 0);
        h.GroundObject = corpse;

        Assert.True(h.Controller.ActivateItem(loot));

        Assert.Equal(new[] { (loot, Player, 0) }, h.BackpackPlacements);
        Assert.Empty(h.Uses);
        Assert.Empty(h.Wields);
    }

    [Fact]
    public void DropOwnedItemOnOpenExternalContainer_SendsPutWithoutOptimisticMove()
    {
        var h = new Harness();
        const uint itemId = 0x50000A33u;
        const uint chest = 0x70000001u;
        ClientObject item = h.AddContained(itemId);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = chest,
            Type = ItemType.Container,
            ItemsCapacity = 24,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Openable,
        });
        h.GroundObject = chest;
        var cell = new UiItemSlot();
        cell.SetItem(itemId, 0u);
        var payload = new ItemDragPayload(itemId, ItemDragSource.Inventory, 0, cell);

        Assert.True(h.Controller.PlaceIn3D(payload, chest));

        Assert.Equal(new[] { (itemId, chest, 0) }, h.Puts);
        Assert.Equal(Pack, item.ContainerId);
    }

    [Fact]
    public void PrimaryClickRouter_distinguishesInactiveSuccessAndConsumedRejection()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = HealthKitUseability;
            item.TargetType = (uint)ItemType.Creature;
        });
        h.AddContained(0x50000A02u, item => item.Type = ItemType.Armor);

        Assert.Equal(ItemPrimaryClickResult.NotActive,
            h.Controller.OfferPrimaryClick(Player));

        h.Controller.ActivateItem(0x50000A01u);
        Assert.Equal(ItemPrimaryClickResult.ConsumedRejected,
            h.Controller.OfferPrimaryClick(0x50000A02u));
        Assert.False(h.Controller.IsTargetModeActive);

        h.Now += 200;
        h.Controller.ActivateItem(0x50000A01u);
        Assert.Equal(ItemPrimaryClickResult.ConsumedSuccess,
            h.Controller.OfferSelfPrimaryClick());
        Assert.Equal(new[] { (0x50000A01u, Player) }, h.UseWithTarget);
    }

    [Fact]
    public void ToolbarUse_withoutSelection_armsOneShotUseMode()
    {
        var h = new Harness();
        h.AddContained(0x50000A10u, item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.UseSelectedOrEnterMode(0u));
        Assert.True(h.Controller.IsAnyTargetModeActive);
        Assert.Equal(InteractionModeKind.Use, h.Controller.InteractionState.Current.Kind);

        Assert.Equal(ItemPrimaryClickResult.ConsumedSuccess,
            h.Controller.OfferPrimaryClick(0x50000A10u));
        Assert.Equal(new[] { 0x50000A10u }, h.Uses);
        Assert.False(h.Controller.IsAnyTargetModeActive);
    }

    [Fact]
    public void ToolbarExamine_usesSelectionOrArmsOneShotExamineMode()
    {
        var h = new Harness();

        Assert.True(h.Controller.ExamineSelectedOrEnterMode(Player));
        Assert.Equal(new[] { Player }, h.Examines);
        Assert.Equal(1, h.Controller.BusyCount);

        Assert.True(h.Controller.ExamineSelectedOrEnterMode(0u));
        Assert.Equal(InteractionModeKind.Examine, h.Controller.InteractionState.Current.Kind);
        Assert.Equal(ItemPrimaryClickResult.ConsumedSuccess,
            h.Controller.OfferPrimaryClick(Pack));
        Assert.Equal(new[] { Player, Pack }, h.Examines);
        Assert.Equal(1, h.Controller.BusyCount);
        Assert.False(h.Controller.IsAnyTargetModeActive);
    }

    [Fact]
    public void BorrowedTransactionOwnerIsExactAndOutlivesController()
    {
        var h = new Harness();

        h.Controller.IncrementBusyCount();

        Assert.Equal(1, h.SharedTransactions.BusyCount);
        h.Controller.Dispose();
        Assert.False(h.SharedTransactions.IsDisposed);

        h.SharedTransactions.ClearBusy();
        Assert.Equal(0, h.SharedTransactions.BusyCount);
        h.SharedTransactions.Dispose();
    }

    [Fact]
    public void BorrowedTransactionOwnerRejectsDifferentObjectTable()
    {
        var objects = new ClientObjectTable();
        using var transactions =
            new InventoryTransactionState(new ClientObjectTable());
        using var runtimeTransactions =
            new RuntimeInteractionTransactionState(transactions);

        Assert.Throws<ArgumentException>(() => new ItemInteractionController(
            objects,
            runtimeTransactions,
            new InteractionState(),
            playerGuid: () => Player,
            sendUse: null,
            sendUseWithTarget: null,
            sendWield: null,
            sendDrop: null));
    }

    [Fact]
    public void AppraisalResponse_releasesOneBusyReferenceAndAcceptsCurrentRefresh()
    {
        var h = new Harness();

        Assert.True(h.Controller.ExamineSelectedOrEnterMode(Player));
        Assert.True(h.Controller.ExamineSelectedOrEnterMode(Pack));
        Assert.Equal(1, h.Controller.BusyCount);

        Assert.False(h.Controller.AcceptAppraisalResponse(Player).Accepted);
        Assert.Equal(1, h.Controller.BusyCount);

        var first = h.Controller.AcceptAppraisalResponse(Pack);
        Assert.True(first.Accepted);
        Assert.True(first.FirstResponse);
        Assert.Equal(0, h.Controller.BusyCount);
        Assert.Equal(Pack, h.Controller.CurrentAppraisalId);

        var refresh = h.Controller.AcceptAppraisalResponse(Pack);
        Assert.True(refresh.Accepted);
        Assert.False(refresh.FirstResponse);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void RefreshCurrentAppraisal_sendsWithoutAcquiringBusyReference()
    {
        var h = new Harness();
        Assert.True(h.Controller.ExamineSelectedOrEnterMode(Pack));
        Assert.True(h.Controller.AcceptAppraisalResponse(Pack).Accepted);

        Assert.True(h.Controller.RefreshCurrentAppraisal());

        Assert.Equal(new[] { Pack, Pack }, h.Examines);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void SelfTarget_sendsUseWithTargetAndClearsTargetMode()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = HealthKitUseability;
            item.TargetType = (uint)ItemType.Creature;
        });

        h.Controller.ActivateItem(0x50000A01u);
        Assert.True(h.Controller.AcquireSelfTarget());

        Assert.Equal(new[] { (0x50000A01u, Player) }, h.UseWithTarget);
        Assert.False(h.Controller.IsTargetModeActive);
    }

    [Fact]
    public void ActivateTargetItemDuringTargetMode_usesClickedItemAsTarget()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = 0x00080008u;               // TARGET_CONTAINED tool
            item.TargetType = (uint)ItemType.Misc;       // kind gate must pass for the send
        });
        h.AddContained(0x50000A02u);

        h.Controller.ActivateItem(0x50000A01u);
        Assert.True(h.Controller.ActivateItem(0x50000A02u));

        Assert.Equal(new[] { (0x50000A01u, 0x50000A02u) }, h.UseWithTarget);
        Assert.False(h.Controller.IsTargetModeActive);
    }


    [Fact]
    public void TargetCompat_wrongKind_refusesAndClearsMode()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = HealthKitUseability;
            item.TargetType = (uint)ItemType.Creature;
        });
        h.AddContained(0x50000A02u, item => item.Type = ItemType.Armor);

        h.Controller.ActivateItem(0x50000A01u);
        Assert.False(h.Controller.ActivateItem(0x50000A02u));

        Assert.Empty(h.UseWithTarget);
        Assert.False(h.Controller.IsTargetModeActive);
    }

    [Fact]
    public void IsCurrentTargetCompatible_appliesKindGateForCursor()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = HealthKitUseability;
            item.TargetType = (uint)ItemType.Creature;
        });
        h.AddContained(0x50000A02u, item => item.Type = ItemType.Armor);

        h.Controller.ActivateItem(0x50000A01u);

        Assert.True(h.Controller.IsCurrentTargetCompatible(Player));
        Assert.False(h.Controller.IsCurrentTargetCompatible(0x50000A02u));
    }

    [Fact]
    public void SelfTarget_requiresSelfBit()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = 0x00200008u;               // TARGET_REMOTE only — no Self bit
            item.TargetType = (uint)ItemType.Creature;
        });

        h.Controller.ActivateItem(0x50000A01u);
        Assert.False(h.Controller.AcquireSelfTarget());
        Assert.Empty(h.UseWithTarget);
    }

    [Fact]
    public void MissingTargetTypeMask_matchesNothing()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item => item.Useability = HealthKitUseability);  // no TargetType on wire

        h.Controller.ActivateItem(0x50000A01u);
        Assert.False(h.Controller.AcquireSelfTarget());
        Assert.Empty(h.UseWithTarget);
    }

    [Fact]
    public void ContainedTarget_refusesNonOwnedWorldObject()
    {
        var h = new Harness();
        h.AddContained(0x50000A01u, item =>
        {
            item.Useability = 0x00080008u;
            item.TargetType = (uint)ItemType.Misc;
        });
        h.Objects.AddOrUpdate(new ClientObject { ObjectId = 0x60000001u, Type = ItemType.Misc });

        h.Controller.ActivateItem(0x50000A01u);
        Assert.False(h.Controller.AcquireTarget(0x60000001u));
        Assert.Empty(h.UseWithTarget);
    }

    [Fact]
    public void DirectUseItem_sendsUse()
    {
        var h = new Harness();
        h.AddContained(0x50000A03u, item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.ActivateItem(0x50000A03u));

        Assert.Equal(new[] { 0x50000A03u }, h.Uses);
    }

    [Fact]
    public void PrimaryUseOfOwnedSalvageTool_requestsSalvagePanel()
    {
        var h = new Harness();
        const uint tool = 0x50000A31u;
        var actions = new List<ItemPolicyAction>();
        h.Controller.PolicyActionRequested += actions.Add;
        h.AddContained(tool, item => item.Type = ItemType.TinkeringTool);

        Assert.True(h.Controller.ActivateItem(tool));

        Assert.Equal(
            [new ItemPolicyAction(ItemPolicyActionKind.OpenSalvage, tool)],
            actions);
    }

    [Fact]
    public void ZeroValuedUseabilityGem_sendsUseLikeBlackmoorsFavor()
    {
        var h = new Harness();
        const uint favor = 0x50000A30u;
        h.AddContained(favor, item =>
        {
            item.Name = "Blackmoor's Favor";
            item.Type = ItemType.Gem;
            item.Useability = ItemUseability.Undef;
        });

        Assert.True(h.Controller.ActivateItem(favor));

        Assert.Equal(new[] { favor }, h.Uses);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void ContainerItem_sendsUseToOpen()
    {
        var h = new Harness();
        h.AddContained(0x50000A04u, item =>
        {
            item.Type = ItemType.Container;
            item.ItemsCapacity = 12;
        });

        Assert.True(h.Controller.ActivateItem(0x50000A04u));

        Assert.Equal(new[] { 0x50000A04u }, h.Uses);
    }

    [Fact]
    public void EquippableItemWithFreeSlot_sendsGetAndWieldAndWaitsForServer()
    {
        var h = new Harness();
        h.AddContained(0x50000A05u, item =>
        {
            item.Type = ItemType.Clothing;
            item.ValidLocations = EquipMask.HeadWear;
        });

        Assert.True(h.Controller.ActivateItem(0x50000A05u));

        Assert.Equal(new[] { (0x50000A05u, (uint)EquipMask.HeadWear) }, h.Wields);
        var equipped = h.Objects.Get(0x50000A05u)!;
        Assert.Equal(Pack, equipped.ContainerId);
        Assert.Equal(EquipMask.None, equipped.CurrentlyEquippedLocation);
    }

    [Fact]
    public void EquippableMultiSlotItemWithFreeSlots_sendsFullCoverageMaskAndWaitsForServer()
    {
        var h = new Harness();
        const EquipMask coatMask =
            EquipMask.ChestWear
            | EquipMask.UpperArmWear
            | EquipMask.LowerArmWear;
        h.AddContained(0x50000A15u, item =>
        {
            item.Type = ItemType.Clothing;
            item.ValidLocations = coatMask;
        });

        Assert.True(h.Controller.ActivateItem(0x50000A15u));

        Assert.Equal(new[] { (0x50000A15u, (uint)coatMask) }, h.Wields);
        var equipped = h.Objects.Get(0x50000A15u)!;
        Assert.Equal(Pack, equipped.ContainerId);
        Assert.Equal(EquipMask.None, equipped.CurrentlyEquippedLocation);
    }

    [Fact]
    public void AutoWearItemWithOverlappingSlotButDifferentPriority_sendsFullMask()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50000AF1u,
            Type = ItemType.Clothing,
            CurrentlyEquippedLocation = EquipMask.UpperArmWear,
            Priority = 0x00000001u,
        });
        h.Objects.MoveItem(0x50000AF1u, Player, -1, EquipMask.UpperArmWear);
        const EquipMask coatMask =
            EquipMask.ChestWear
            | EquipMask.UpperArmWear
            | EquipMask.LowerArmWear;
        h.AddContained(0x50000A16u, item =>
        {
            item.Type = ItemType.Clothing;
            item.ValidLocations = coatMask;
            item.Priority = 0x00000002u;
        });

        Assert.True(h.Controller.ActivateItem(0x50000A16u));

        Assert.Equal(new[] { (0x50000A16u, (uint)coatMask) }, h.Wields);
        Assert.Equal(EquipMask.None, h.Objects.Get(0x50000A16u)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void AutoWearItemWithOverlappingSlotAndPriority_sendsNothing()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50000AF2u,
            Name = "Chainmail Hauberk",
            Type = ItemType.Clothing,
            CurrentlyEquippedLocation = EquipMask.UpperArmWear,
            Priority = 0x00000004u,
        });
        h.Objects.MoveItem(0x50000AF2u, Player, -1, EquipMask.UpperArmWear);
        const EquipMask coatMask =
            EquipMask.ChestWear
            | EquipMask.UpperArmWear
            | EquipMask.LowerArmWear;
        h.AddContained(0x50000A17u, item =>
        {
            item.Type = ItemType.Clothing;
            item.ValidLocations = coatMask;
            item.Priority = 0x00000004u;
        });

        Assert.False(h.Controller.ActivateItem(0x50000A17u));

        Assert.Empty(h.Wields);
        Assert.Equal(Pack, h.Objects.Get(0x50000A17u)!.ContainerId);
        Assert.Equal(
            new[] { "You must remove your Chainmail Hauberk to wear that" },
            h.SystemMessages);
        Assert.Empty(h.Toasts);
    }

    [Fact]
    public void EquippableItemWithNoFreeSlot_movesBlockerThenWieldsAfterServerConfirm()
    {
        var h = new Harness();
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x50000AF0u,
            Name = "Old Shield",
            Type = ItemType.Armor,
            CurrentlyEquippedLocation = EquipMask.Shield,
        });
        h.Objects.MoveItem(0x50000AF0u, Player, -1, EquipMask.Shield);
        h.AddContained(0x50000A06u, item =>
        {
            item.Type = ItemType.Armor;
            item.CombatUse = 4;
            item.ValidLocations = EquipMask.Shield;
            item.Useability = ItemUseability.No;
        });

        bool activated = h.Controller.ActivateItem(0x50000A06u);

        Assert.True(activated);
        Assert.Empty(h.Wields);
        Assert.Equal(new[] { (0x50000AF0u, Player, 0) }, h.Puts);
        Assert.Equal(new[] { "Moving Old Shield to your backpack" }, h.SystemMessages);
        Assert.Equal(Pack, h.Objects.Get(0x50000A06u)!.ContainerId);

        Assert.True(h.Objects.ApplyConfirmedServerMove(0x50000AF0u, Player, 0u, 0));

        Assert.Equal(
            new[] { (0x50000A06u, (uint)EquipMask.Shield) },
            h.Wields);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WeaponReplacement_inPeaceOrWar_unwieldsThenWieldsAfterServerConfirm(
        bool nonCombatMode)
    {
        var h = new Harness { NonCombatMode = nonCombatMode };
        const uint sword = 0x50000B01u;
        const uint bow = 0x50000B02u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Name = "Sword",
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        h.Objects.MoveItem(sword, Player, -1, EquipMask.MeleeWeapon);
        h.AddContained(bow, item =>
        {
            item.Name = "Bow";
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 1;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));

        Assert.Equal(new[] { (sword, Player, 0) }, h.Puts);
        Assert.Empty(h.Wields);
        Assert.Equal(EquipMask.MeleeWeapon,
            h.Objects.Get(sword)!.CurrentlyEquippedLocation);
        Assert.Equal(Pack, h.Objects.Get(bow)!.ContainerId);

        Assert.True(h.Objects.ApplyConfirmedServerMove(sword, Player, 0u, 0));

        Assert.Equal(new[] { (bow, (uint)EquipMask.MissileWeapon) }, h.Wields);
        Assert.Equal(EquipMask.None,
            h.Objects.Get(sword)!.CurrentlyEquippedLocation);
        Assert.Equal(EquipMask.None,
            h.Objects.Get(bow)!.CurrentlyEquippedLocation);
        Assert.Equal(Pack, h.Objects.Get(bow)!.ContainerId);
        Assert.True(h.Objects.ApplyConfirmedServerWield(
            bow, Player, EquipMask.MissileWeapon));
        Assert.Equal(EquipMask.MissileWeapon,
            h.Objects.Get(bow)!.CurrentlyEquippedLocation);
    }

    [Theory]
    [InlineData(CombatMode.NonCombat, false)]
    [InlineData(CombatMode.Magic, true)]
    public void WandToBow_reassertsMissileModeOnlyForActiveCombatTransaction(
        CombatMode startingMode,
        bool expectsMissileMode)
    {
        var h = new Harness();
        h.Combat.SetCombatMode(startingMode);
        const uint wand = 0x50000B91u;
        const uint bow = 0x50000B92u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = wand,
            Name = "Training Wand",
            Type = ItemType.Caster,
            CombatUse = 2,
            ValidLocations = EquipMask.Held,
        });
        h.Objects.MoveItem(wand, Player, -1, EquipMask.Held);
        h.AddContained(bow, item =>
        {
            item.Name = "Shortbow";
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 2;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        Assert.Equal(new[] { (wand, Player, 0) }, h.Puts);

        h.Combat.SetCombatMode(CombatMode.Melee);
        Assert.True(h.Objects.ApplyConfirmedServerMove(wand, Player, 0u, 0));
        Assert.Equal(new[] { (bow, (uint)EquipMask.MissileWeapon) }, h.Wields);
        Assert.Empty(h.CombatModeRequests);

        Assert.True(h.Objects.ApplyConfirmedServerWield(
            bow,
            Player,
            EquipMask.MissileWeapon));
        Assert.Empty(h.CombatModeRequests);

        if (expectsMissileMode)
        {
            h.Combat.SetCombatMode(CombatMode.NonCombat);
            Assert.Empty(h.CombatModeRequests);
            h.Combat.SetCombatMode(CombatMode.Missile);
            Assert.Empty(h.CombatModeRequests);
            h.Combat.SetCombatMode(CombatMode.NonCombat);
        }
        else
        {
            h.Combat.SetCombatMode(CombatMode.Missile);
        }

        if (expectsMissileMode)
            Assert.Equal(new[] { CombatMode.Missile }, h.CombatModeRequests);
        else
            Assert.Empty(h.CombatModeRequests);
    }

    [Fact]
    public void BowToWand_inActiveCombat_reassertsMagicAfterAceTrailingPeace()
    {
        var h = new Harness();
        h.Combat.SetCombatMode(CombatMode.Missile);
        const uint bow = 0x50000B93u;
        const uint wand = 0x50000B94u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            Name = "Shortbow",
            Type = ItemType.MissileWeapon,
            CombatUse = 2,
            ValidLocations = EquipMask.MissileWeapon,
        });
        h.Objects.MoveItem(bow, Player, -1, EquipMask.MissileWeapon);
        h.AddContained(wand, item =>
        {
            item.Name = "Training Wand";
            item.Type = ItemType.Caster;
            item.CombatUse = 2;
            item.ValidLocations = EquipMask.Held;
        });

        Assert.True(h.Controller.ActivateItem(wand));
        Assert.Equal(new[] { (bow, Player, 0) }, h.Puts);

        h.Combat.SetCombatMode(CombatMode.Melee);
        h.Combat.SetCombatMode(CombatMode.NonCombat);
        Assert.True(h.Objects.ApplyConfirmedServerMove(bow, Player, 0u, 0));
        Assert.Equal(new[] { (wand, (uint)EquipMask.Held) }, h.Wields);

        Assert.True(h.Objects.ApplyConfirmedServerWield(
            wand,
            Player,
            EquipMask.Held));
        h.Combat.SetCombatMode(CombatMode.Magic);
        Assert.Empty(h.CombatModeRequests);

        h.Combat.SetCombatMode(CombatMode.NonCombat);

        Assert.Equal(new[] { CombatMode.Magic }, h.CombatModeRequests);
    }

    [Fact]
    public void ExplicitCombatRequest_cancelsPendingAutoWieldSettlement()
    {
        var h = new Harness();
        h.Combat.SetCombatMode(CombatMode.Missile);
        const uint bow = 0x50000B95u;
        const uint wand = 0x50000B96u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            Type = ItemType.MissileWeapon,
            ValidLocations = EquipMask.MissileWeapon,
        });
        h.Objects.MoveItem(bow, Player, -1, EquipMask.MissileWeapon);
        h.AddContained(wand, item =>
        {
            item.Type = ItemType.Caster;
            item.ValidLocations = EquipMask.Held;
        });

        Assert.True(h.Controller.ActivateItem(wand));
        h.Combat.SetCombatMode(CombatMode.NonCombat);
        Assert.True(h.Objects.ApplyConfirmedServerMove(bow, Player, 0u, 0));
        Assert.True(h.Objects.ApplyConfirmedServerWield(
            wand,
            Player,
            EquipMask.Held));
        h.Combat.SetCombatMode(CombatMode.Magic);

        h.Controller.NotifyExplicitCombatModeRequest();
        h.Combat.SetCombatMode(CombatMode.NonCombat);

        Assert.Empty(h.CombatModeRequests);
    }

    [Fact]
    public void ActiveWeaponReplacement_serverSettlesDirectly_doesNotReassertLaterPeace()
    {
        var h = new Harness();
        h.Combat.SetCombatMode(CombatMode.Magic);
        const uint wand = 0x50000BA1u;
        const uint bow = 0x50000BA2u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = wand,
            Type = ItemType.Caster,
            CombatUse = 2,
            ValidLocations = EquipMask.Held,
        });
        h.Objects.MoveItem(wand, Player, -1, EquipMask.Held);
        h.AddContained(bow, item =>
        {
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 2;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        Assert.True(h.Objects.ApplyConfirmedServerMove(wand, Player, 0u, 0));
        Assert.True(h.Objects.ApplyConfirmedServerWield(
            bow, Player, EquipMask.MissileWeapon));

        h.Combat.SetCombatMode(CombatMode.Missile);
        h.Combat.SetCombatMode(CombatMode.NonCombat);

        Assert.Empty(h.CombatModeRequests);
    }

    [Fact]
    public void PreviouslyWieldedBow_AfterAuthoritativeUnwield_DoubleClickWieldsAgain()
    {
        var h = new Harness();
        const uint bow = 0x50000B03u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            Name = "Bow",
            Type = ItemType.MissileWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MissileWeapon,
            ContainerId = Player,
            WielderId = Player,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
        });

        Assert.True(h.Objects.ApplyServerMove(
            bow,
            Pack,
            newWielderId: 0u,
            newSlot: 0));

        Assert.True(h.Controller.ActivateItem(bow));

        Assert.Equal(new[] { (bow, (uint)EquipMask.MissileWeapon) }, h.Wields);
    }

    [Fact]
    public void WeaponReplacement_doesNotOverlapRequestsWhileAwaitingServer()
    {
        var h = new Harness();
        const uint sword = 0x50000B11u;
        const uint bow = 0x50000B12u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        h.Objects.MoveItem(sword, Player, -1, EquipMask.MeleeWeapon);
        h.AddContained(bow, item =>
        {
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 1;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        h.Now += 200;
        Assert.False(h.Controller.ActivateItem(bow));

        Assert.Single(h.Puts);
        Assert.Empty(h.Wields);
    }

    [Fact]
    public void WeaponReplacement_doesNotOverlapWhileAwaitingFinalWieldConfirmation()
    {
        var h = new Harness();
        const uint sword = 0x50000B15u;
        const uint bow = 0x50000B16u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        h.Objects.MoveItem(sword, Player, -1, EquipMask.MeleeWeapon);
        h.AddContained(bow, item =>
        {
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 2;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        Assert.True(h.Objects.ApplyConfirmedServerMove(sword, Player, 0u, 0));
        Assert.Single(h.Wields);

        h.Now += 200;
        Assert.False(h.Controller.ActivateItem(sword));
        Assert.Single(h.Puts);
        Assert.Single(h.Wields);

        Assert.True(h.Objects.WieldItemOptimistic(
            bow, Player, EquipMask.MissileWeapon));

        Assert.True(h.Objects.ApplyConfirmedServerWield(
            bow,
            Player,
            EquipMask.MissileWeapon));
        h.Now += 200;
        Assert.True(h.Controller.ActivateItem(sword));
        Assert.Equal(
            new[] { (sword, Player, 0), (bow, Player, 0) },
            h.Puts);
    }

    [Fact]
    public void WeaponReplacement_serverReject_clearsPendingTransactionForRetry()
    {
        var h = new Harness();
        const uint sword = 0x50000B21u;
        const uint bow = 0x50000B22u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        h.Objects.MoveItem(sword, Player, -1, EquipMask.MeleeWeapon);
        h.AddContained(bow, item =>
        {
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 1;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        Assert.False(h.Objects.RejectMove(sword, 0x04FFu));
        h.Now += 200;
        Assert.True(h.Controller.ActivateItem(bow));

        Assert.Equal(2, h.Puts.Count);
        Assert.Empty(h.Wields);
        Assert.Equal(EquipMask.MeleeWeapon,
            h.Objects.Get(sword)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void WeaponReplacement_bow_removesPrimaryThenIncompatibleShield()
    {
        var h = new Harness();
        const uint sword = 0x50000B31u;
        const uint shield = 0x50000B32u;
        const uint bow = 0x50000B33u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        h.Objects.MoveItem(sword, Player, -1, EquipMask.MeleeWeapon);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = shield,
            Type = ItemType.Armor,
            CombatUse = 4,
            ValidLocations = EquipMask.Shield,
        });
        h.Objects.MoveItem(shield, Player, -1, EquipMask.Shield);
        h.AddContained(bow, item =>
        {
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 2;
            item.AmmoType = 1;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        Assert.Equal(new[] { (sword, Player, 0) }, h.Puts);

        Assert.True(h.Objects.ApplyConfirmedServerMove(sword, Player, 0u, 0));
        Assert.Equal(
            new[] { (sword, Player, 0), (shield, Player, 0) },
            h.Puts);
        Assert.Empty(h.Wields);

        Assert.True(h.Objects.ApplyConfirmedServerMove(shield, Player, 0u, 0));
        Assert.Equal(new[] { (bow, (uint)EquipMask.MissileWeapon) }, h.Wields);
    }

    [Fact]
    public void WeaponReplacement_bow_removesMismatchedAmmoAfterPrimaryBlocker()
    {
        var h = new Harness();
        const uint sword = 0x50000B41u;
        const uint arrows = 0x50000B42u;
        const uint bow = 0x50000B43u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = sword,
            Type = ItemType.MeleeWeapon,
            CombatUse = 1,
            ValidLocations = EquipMask.MeleeWeapon,
        });
        h.Objects.MoveItem(sword, Player, -1, EquipMask.MeleeWeapon);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = arrows,
            Type = ItemType.MissileWeapon,
            CombatUse = 3,
            AmmoType = 2,
            ValidLocations = EquipMask.MissileAmmo,
        });
        h.Objects.MoveItem(arrows, Player, -1, EquipMask.MissileAmmo);
        h.AddContained(bow, item =>
        {
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 2;
            item.AmmoType = 1;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.ActivateItem(bow));
        Assert.True(h.Objects.ApplyConfirmedServerMove(sword, Player, 0u, 0));
        Assert.Equal(
            new[] { (sword, Player, 0), (arrows, Player, 0) },
            h.Puts);

        Assert.True(h.Objects.ApplyConfirmedServerMove(arrows, Player, 0u, 0));
        Assert.Equal(new[] { (bow, (uint)EquipMask.MissileWeapon) }, h.Wields);
    }

    [Fact]
    public void PaperdollWeaponSwitch_toSword_keepsEquippedArrows()
    {
        var h = new Harness();
        const uint bow = 0x50000B51u;
        const uint arrows = 0x50000B52u;
        const uint sword = 0x50000B53u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            Name = "Shortbow",
            Type = ItemType.MissileWeapon,
            CombatUse = 2,
            AmmoType = 1,
            ValidLocations = EquipMask.MissileWeapon,
        });
        h.Objects.MoveItem(bow, Player, -1, EquipMask.MissileWeapon);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = arrows,
            Name = "Arrows",
            Type = ItemType.MissileWeapon,
            CombatUse = 3,
            AmmoType = 1,
            ValidLocations = EquipMask.MissileAmmo,
        });
        h.Objects.MoveItem(arrows, Player, -1, EquipMask.MissileAmmo);
        h.AddContained(sword, item =>
        {
            item.Name = "Katar";
            item.Type = ItemType.MeleeWeapon;
            item.CombatUse = 1;
            item.ValidLocations = EquipMask.MeleeWeapon;
        });

        Assert.True(h.Controller.WieldFromPaperdoll(sword, EquipMask.MeleeWeapon));
        Assert.Equal(new[] { (bow, Player, 0) }, h.Puts);
        Assert.Empty(h.Wields);
        Assert.Equal(new[] { "Moving Shortbow to your backpack" }, h.SystemMessages);

        Assert.True(h.Objects.ApplyConfirmedServerMove(bow, Player, 0u, 0));

        Assert.Equal(new[] { (sword, (uint)EquipMask.MeleeWeapon) }, h.Wields);
        Assert.Equal(EquipMask.MissileAmmo,
            h.Objects.Get(arrows)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void PaperdollWeaponSwitch_fromWandToSword_movesHeldCasterFirst()
    {
        var h = new Harness();
        const uint wand = 0x50000B54u;
        const uint sword = 0x50000B55u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = wand,
            Name = "Wand",
            Type = ItemType.Caster,
            ValidLocations = EquipMask.Held,
        });
        h.Objects.MoveItem(wand, Player, -1, EquipMask.Held);
        h.AddContained(sword, item =>
        {
            item.Name = "Katar";
            item.Type = ItemType.MeleeWeapon;
            item.CombatUse = 1;
            item.ValidLocations = EquipMask.MeleeWeapon;
        });

        Assert.True(h.Controller.WieldFromPaperdoll(sword, EquipMask.MeleeWeapon));
        Assert.Equal(new[] { (wand, Player, 0) }, h.Puts);
        Assert.Empty(h.Wields);
        Assert.Equal(new[] { "Moving Wand to your backpack" }, h.SystemMessages);

        Assert.True(h.Objects.ApplyConfirmedServerMove(wand, Player, 0u, 0));

        Assert.Equal(new[] { (sword, (uint)EquipMask.MeleeWeapon) }, h.Wields);
    }

    [Fact]
    public void PaperdollWeaponSwitch_toCrossbow_movesIncompatibleArrowsAfterBow()
    {
        var h = new Harness();
        const uint bow = 0x50000B61u;
        const uint arrows = 0x50000B62u;
        const uint crossbow = 0x50000B63u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            Name = "Shortbow",
            Type = ItemType.MissileWeapon,
            CombatUse = 2,
            AmmoType = 1,
            ValidLocations = EquipMask.MissileWeapon,
        });
        h.Objects.MoveItem(bow, Player, -1, EquipMask.MissileWeapon);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = arrows,
            Name = "Arrows",
            Type = ItemType.MissileWeapon,
            CombatUse = 3,
            AmmoType = 1,
            ValidLocations = EquipMask.MissileAmmo,
        });
        h.Objects.MoveItem(arrows, Player, -1, EquipMask.MissileAmmo);
        h.AddContained(crossbow, item =>
        {
            item.Name = "Heavy Crossbow";
            item.Type = ItemType.MissileWeapon;
            item.CombatUse = 2;
            item.AmmoType = 2;
            item.ValidLocations = EquipMask.MissileWeapon;
        });

        Assert.True(h.Controller.WieldFromPaperdoll(
            crossbow, EquipMask.MissileWeapon));
        Assert.Equal(new[] { (bow, Player, 0) }, h.Puts);

        Assert.True(h.Objects.ApplyConfirmedServerMove(bow, Player, 0u, 0));

        Assert.Equal(
            new[] { (bow, Player, 0), (arrows, Player, 0) },
            h.Puts);
        Assert.Empty(h.Wields);

        Assert.True(h.Objects.ApplyConfirmedServerMove(arrows, Player, 0u, 0));

        Assert.Equal(
            new[] { (crossbow, (uint)EquipMask.MissileWeapon) },
            h.Wields);
        Assert.Equal(
            new[]
            {
                "Moving Shortbow to your backpack",
                "Moving Arrows to your backpack",
            },
            h.SystemMessages);
    }

    [Fact]
    public void PaperdollDrop_relocatesAlreadyEquippedItemToExplicitCompatibleSlot()
    {
        var h = new Harness();
        const uint ring = 0x50000B71u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = ring,
            Name = "Ring",
            Type = ItemType.Jewelry,
            ValidLocations = EquipMask.FingerWearLeft | EquipMask.FingerWearRight,
        });
        h.Objects.MoveItem(ring, Player, -1, EquipMask.FingerWearLeft);

        Assert.True(h.Controller.WieldFromPaperdoll(ring, EquipMask.FingerWearRight));

        Assert.Equal(
            new[] { (ring, (uint)EquipMask.FingerWearRight) },
            h.Wields);
        Assert.Equal(
            EquipMask.FingerWearLeft,
            h.Objects.Get(ring)!.CurrentlyEquippedLocation);
        Assert.True(h.Objects.ApplyConfirmedServerWield(
            ring, Player, EquipMask.FingerWearRight));
        Assert.Equal(EquipMask.FingerWearRight,
            h.Objects.Get(ring)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void PaperdollDrop_movesExplicitDestinationBlockerBeforeRelocatingEquippedItem()
    {
        var h = new Harness();
        const uint leftRing = 0x50000B72u;
        const uint rightRing = 0x50000B73u;
        EquipMask valid = EquipMask.FingerWearLeft | EquipMask.FingerWearRight;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = leftRing,
            Name = "Left Ring",
            Type = ItemType.Jewelry,
            ValidLocations = valid,
        });
        h.Objects.MoveItem(leftRing, Player, -1, EquipMask.FingerWearLeft);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = rightRing,
            Name = "Right Ring",
            Type = ItemType.Jewelry,
            ValidLocations = valid,
        });
        h.Objects.MoveItem(rightRing, Player, -1, EquipMask.FingerWearRight);

        Assert.True(h.Controller.WieldFromPaperdoll(leftRing, EquipMask.FingerWearRight));
        Assert.Equal(new[] { (rightRing, Player, 0) }, h.Puts);
        Assert.Empty(h.Wields);
        Assert.Equal(new[] { "Moving Right Ring to your backpack" }, h.SystemMessages);

        Assert.True(h.Objects.ApplyConfirmedServerMove(rightRing, Player, 0u, 0));

        Assert.Equal(
            new[] { (leftRing, (uint)EquipMask.FingerWearRight) },
            h.Wields);
        Assert.Equal(
            EquipMask.FingerWearLeft,
            h.Objects.Get(leftRing)!.CurrentlyEquippedLocation);
        Assert.True(h.Objects.ApplyConfirmedServerWield(
            leftRing, Player, EquipMask.FingerWearRight));
        Assert.Equal(EquipMask.FingerWearRight,
            h.Objects.Get(leftRing)!.CurrentlyEquippedLocation);
    }

    [Fact]
    public void ActivateItem_whenEveryCompatibleSlotIsOccupied_movesRetailPreferredBlockerThenWields()
    {
        var h = new Harness();
        const uint leftRing = 0x50000B74u;
        const uint rightRing = 0x50000B75u;
        const uint requestedRing = 0x50000B76u;
        EquipMask valid = EquipMask.FingerWearLeft | EquipMask.FingerWearRight;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = leftRing,
            Name = "Left Ring",
            Type = ItemType.Jewelry,
            ValidLocations = valid,
        });
        h.Objects.MoveItem(leftRing, Player, -1, EquipMask.FingerWearLeft);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = rightRing,
            Name = "Right Ring",
            Type = ItemType.Jewelry,
            ValidLocations = valid,
        });
        h.Objects.MoveItem(rightRing, Player, -1, EquipMask.FingerWearRight);
        h.AddContained(requestedRing, item =>
        {
            item.Name = "New Ring";
            item.Type = ItemType.Jewelry;
            item.ValidLocations = valid;
        });

        Assert.True(h.Controller.ActivateItem(requestedRing));
        Assert.Equal(new[] { (leftRing, Player, 0) }, h.Puts);
        Assert.Empty(h.Wields);
        Assert.Equal(new[] { "Moving Left Ring to your backpack" }, h.SystemMessages);

        Assert.True(h.Objects.ApplyConfirmedServerMove(leftRing, Player, 0u, 0));

        Assert.Equal(
            new[] { (requestedRing, (uint)EquipMask.FingerWearLeft) },
            h.Wields);
    }

    [Fact]
    public void BlocksUseOfShield_matchesRetailCombatUseAndAmmoRules()
    {
        Assert.True(AutoWieldController.BlocksUseOfShield(new ClientObject
        {
            CombatUse = 2,
            AmmoType = 1,
        }));
        Assert.False(AutoWieldController.BlocksUseOfShield(new ClientObject
        {
            CombatUse = 2,
            AmmoType = 0,
        }));
        Assert.True(AutoWieldController.BlocksUseOfShield(new ClientObject
        {
            CombatUse = 5,
        }));
        Assert.True(AutoWieldController.BlocksUseOfShield(new ClientObject
        {
            Type = ItemType.Caster,
        }));
    }

    [Fact]
    public void InventoryDragOutsideUi_sendsDropAndWaitsForServerPlacement()
    {
        var h = new Harness();
        h.AddContained(0x50000A07u);
        var payload = new ItemDragPayload(
            0x50000A07u,
            ItemDragSource.Inventory,
            SourceSlot: 0,
            SourceCell: new UiItemSlot());

        Assert.True(h.Controller.DropToWorld(payload));

        Assert.Equal(new[] { 0x50000A07u }, h.Drops);
        Assert.Equal(Pack, h.Objects.Get(0x50000A07u)!.ContainerId);
    }

    [Fact]
    public void RefusedDrop_ComposesCantBeDroppedLine_AsClientLocal()
    {
        var h = new Harness();
        const uint item = 0x50000A07u;
        h.AddContained(item);
        Assert.True(h.Controller.DropToWorld(new ItemDragPayload(
            item,
            ItemDragSource.Inventory,
            SourceSlot: 0,
            SourceCell: new UiItemSlot())));

        h.Objects.RejectMove(item, 0x426u);

        (string text, RetailLogTextType type) = Assert.Single(h.InterfaceTexts);
        Assert.Equal($"The Item {item:X} can't be dropped", text);
        Assert.Equal(RetailLogTextType.ClientLocal, type);

        h.InterfaceTexts.Clear();
        h.Objects.RejectMove(item, 0x426u);
        Assert.Empty(h.InterfaceTexts);
    }

    [Fact]
    public void InventoryDragOnNpc_sendsGiveWithoutOptimisticInventoryMutation()
    {
        var h = new Harness();
        const uint item = 0x50000A71u;
        const uint npc = 0x50001001u;
        h.AddContained(item);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = npc,
            Name = "Starter Exit Warden",
            Type = ItemType.Creature,
        });
        var payload = new ItemDragPayload(
            item, ItemDragSource.Inventory, SourceSlot: 0, SourceCell: new UiItemSlot());

        Assert.True(h.Controller.PlaceIn3D(payload, npc));

        Assert.Equal(new[] { (npc, item, 1u) }, h.Gives);
        Assert.Empty(h.Drops);
        Assert.Equal(Pack, h.Objects.Get(item)!.ContainerId);
        Assert.Equal(1, h.Objects.Get(item)!.StackSize);
    }

    [Fact]
    public void SelectedPartialStackDragOnNpc_sendsSelectedGiveAmount()
    {
        var h = new Harness();
        const uint item = 0x50000A72u;
        const uint npc = 0x50001002u;
        h.AddContained(item, value => value.StackSize = 10);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = npc,
            Type = ItemType.Creature,
        });
        h.SelectedObject = item;
        h.SplitQuantity.Reset(10u);
        h.SplitQuantity.SetValue(3u);
        var payload = new ItemDragPayload(
            item, ItemDragSource.Inventory, SourceSlot: 0, SourceCell: new UiItemSlot());

        Assert.True(h.Controller.PlaceIn3D(payload, npc));

        Assert.Equal(new[] { (npc, item, 3u) }, h.Gives);
        Assert.Equal(10, h.Objects.Get(item)!.StackSize);
        Assert.Equal(Pack, h.Objects.Get(item)!.ContainerId);
    }

    [Fact]
    public void InventoryDragOnNonCreature_usesRetailGroundFallback()
    {
        var h = new Harness();
        const uint item = 0x50000A73u;
        const uint scenery = 0x50002001u;
        h.AddContained(item);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = scenery,
            Type = ItemType.Misc,
        });
        var payload = new ItemDragPayload(
            item, ItemDragSource.Inventory, SourceSlot: 0, SourceCell: new UiItemSlot());

        Assert.True(h.Controller.PlaceIn3D(payload, scenery));

        Assert.Empty(h.Gives);
        Assert.Equal(new[] { item }, h.Drops);
        Assert.Equal(Pack, h.Objects.Get(item)!.ContainerId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InventoryDragOnPlayer_honorsRetailSecureTradeOption(
        bool opensSecureTrade,
        bool sendsGive)
    {
        var h = new Harness { DragOnPlayerOpensSecureTrade = opensSecureTrade };
        const uint item = 0x50000A74u;
        const uint targetPlayer = 0x50003001u;
        h.AddContained(item);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = targetPlayer,
            Type = ItemType.Creature,
            PublicWeenieBitfield = (uint)PublicWeenieFlags.Player,
        });
        var payload = new ItemDragPayload(
            item, ItemDragSource.Inventory, SourceSlot: 0, SourceCell: new UiItemSlot());

        var tradeRequests = new List<(uint Partner, uint Item)>();
        h.Controller.SecureTradeRequested += (partner, dragged) =>
            tradeRequests.Add((partner, dragged));

        bool result = h.Controller.PlaceIn3D(payload, targetPlayer);

        Assert.Equal(sendsGive, result);
        if (sendsGive)
        {
            Assert.Equal(new[] { (targetPlayer, item, 1u) }, h.Gives);
            Assert.Empty(tradeRequests);
        }
        else
        {
            Assert.Empty(h.Gives);
            Assert.Equal([(targetPlayer, item)], tradeRequests);
        }
        Assert.Equal(Pack, h.Objects.Get(item)!.ContainerId);
    }

    [Fact]
    public void SelectedPartialStackDragOutsideUi_splitsWithoutMovingOriginal()
    {
        var h = new Harness();
        h.AddContained(0x50000A70u, item => item.StackSize = 10);
        WorldDropDispatch? dispatched = null;
        h.Controller.WorldDropDispatched += value => dispatched = value;
        h.SelectedObject = 0x50000A70u;
        h.SplitQuantity.Reset(10u);
        h.SplitQuantity.SetValue(1u);
        var payload = new ItemDragPayload(
            0x50000A70u,
            ItemDragSource.Inventory,
            SourceSlot: 0,
            SourceCell: new UiItemSlot());

        Assert.True(h.Controller.DropToWorld(payload));

        Assert.Equal(new[] { (0x50000A70u, 1u) }, h.SplitDrops);
        Assert.Empty(h.Drops);
        Assert.Equal(Pack, h.Objects.Get(0x50000A70u)!.ContainerId);
        Assert.Equal(10, h.Objects.Get(0x50000A70u)!.StackSize);
        Assert.NotNull(dispatched);
        Assert.Equal(InventoryRequestKind.SplitToWorld, dispatched.Value.Request.Kind);
        Assert.Equal(0x50000A70u, dispatched.Value.Request.ItemId);
        Assert.Equal(1u, dispatched.Value.Amount);
    }

    [Fact]
    public void ToolbarShortcutDragOutsideUi_doesNotDropRealItem()
    {
        var h = new Harness();
        h.AddContained(0x50000A08u);
        var payload = new ItemDragPayload(
            0x50000A08u,
            ItemDragSource.ShortcutBar,
            SourceSlot: 0,
            SourceCell: new UiItemSlot());

        Assert.False(h.Controller.DropToWorld(payload));

        Assert.Empty(h.Drops);
        Assert.Equal(Pack, h.Objects.Get(0x50000A08u)!.ContainerId);
    }

    [Fact]
    public void ActivateItem_appliesRetailUseThrottle()
    {
        var h = new Harness();
        h.AddContained(0x50000A09u, item => item.Useability = ItemUseability.Contained);

        h.Controller.ActivateItem(0x50000A09u);
        h.Controller.CompleteUse(0);
        h.Now += 199;
        h.Controller.ActivateItem(0x50000A09u);
        h.Now += 1;
        h.Controller.ActivateItem(0x50000A09u);

        Assert.Equal(new[] { 0x50000A09u, 0x50000A09u }, h.Uses);
    }

    [Fact]
    public void UseDone_releasesRetailBusyGate()
    {
        var h = new Harness();
        h.AddContained(0x50000A0Au, item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.ActivateItem(0x50000A0Au));
        h.Now += 200;
        Assert.False(h.Controller.ActivateItem(0x50000A0Au));
        Assert.Equal(1, h.Controller.BusyCount);

        h.Controller.CompleteUse(0);
        h.Now += 200;

        Assert.True(h.Controller.ActivateItem(0x50000A0Au));
        Assert.Equal(2, h.Uses.Count);
    }

    [Fact]
    public void DeferredUseCancellationReleasesExactlyItsBusyReference()
    {
        ItemUseRequestReservation? pending = null;
        var h = new Harness((_, reservation) => pending = reservation);
        h.AddContained(
            0x50000A0Bu,
            item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.ActivateItem(0x50000A0Bu));
        Assert.Equal(1, h.Controller.BusyCount);

        Assert.NotNull(pending);
        pending.CancelBeforeDispatch();
        pending.CancelBeforeDispatch();

        Assert.Equal(0, h.Controller.BusyCount);
        Assert.True(h.Controller.CanMakeInventoryRequest);
    }

    [Fact]
    public void DispatchedUseReservationIsReleasedOnlyByUseDone()
    {
        ItemUseRequestReservation? pending = null;
        var h = new Harness((_, reservation) => pending = reservation);
        h.AddContained(
            0x50000A0Cu,
            item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.ActivateItem(0x50000A0Cu));
        Assert.NotNull(pending);
        pending.MarkDispatched();
        pending.CancelBeforeDispatch();

        Assert.Equal(1, h.Controller.BusyCount);

        h.Controller.CompleteUse(0u);

        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void SessionResetInvalidatesLateUseReservationResolution()
    {
        ItemUseRequestReservation? stale = null;
        var h = new Harness((_, reservation) => stale = reservation);
        h.AddContained(
            0x50000A0Du,
            item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.ActivateItem(0x50000A0Du));
        Assert.Equal(1, h.Controller.BusyCount);

        h.Controller.ResetSession();
        stale!.CancelBeforeDispatch();

        Assert.Equal(0, h.Controller.BusyCount);
        Assert.True(h.Controller.CanMakeInventoryRequest);
    }

    [Fact]
    public void KeyboardPickup_PublishesPendingDestinationBeforeRequest()
    {
        var h = new Harness();
        const uint item = 0x70000A0Bu;
        var pending = new List<PendingBackpackPlacement>();
        h.Controller.PendingBackpackPlacementRequested += pending.Add;

        Assert.True(h.Controller.PlaceWorldItemInBackpack(item));

        Assert.Collection(
            pending,
            placement =>
            {
                Assert.NotEqual(0u, placement.Token);
                Assert.Equal(item, placement.ItemId);
                Assert.Equal(Player, placement.ContainerId);
                Assert.Equal(0, placement.Placement);
            });
        Assert.Equal(new[] { (item, Player, 0) }, h.BackpackPlacements);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var request));
        Assert.Equal(InventoryRequestKind.Pickup, request.Kind);
        Assert.False(request.Dispatched);

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.Pickup,
            item,
            static () => true,
            pending[0].Token));
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out request));
        Assert.True(request.Dispatched);
    }

    [Fact]
    public void PendingRequestSerializesEveryLaterItemWithoutSecondWire()
    {
        var h = new Harness();
        const uint item = 0x70000A0Du;
        const uint laterItem = 0x70000A0Eu;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Name = "Loot",
            Type = ItemType.Misc,
        });
        var requested = new List<PendingBackpackPlacement>();
        var cancelled = new List<PendingBackpackPlacement>();
        h.Controller.PendingBackpackPlacementRequested += requested.Add;
        h.Controller.PendingBackpackPlacementCancelled += cancelled.Add;

        Assert.True(h.Controller.PlaceWorldItemInBackpack(item));
        PendingBackpackPlacement first = Assert.Single(requested);
        Assert.True(h.Controller.PlaceWorldItemInBackpack(item));
        Assert.True(h.Controller.PlaceWorldItemInBackpack(laterItem));

        Assert.Single(requested);
        Assert.Empty(cancelled);
        Assert.Equal(new[] { (item, Player, 0) }, h.BackpackPlacements);
        Assert.Equal(
            new[]
            {
                "Already attempting to place Loot here",
                "Already attempting to place Loot here",
            },
            h.SystemMessages);
        Assert.True(h.Controller.TryGetPendingBackpackPlacement(item, out var current));
        Assert.Equal(first, current);

        h.Objects.RejectMove(item, weenieError: 0x29u);
        Assert.False(h.Controller.TryGetPendingBackpackPlacement(item, out _));
        Assert.Empty(cancelled);
    }

    [Fact]
    public void BusyInventoryTransactionPreventsPickupReservationAndWireRequest()
    {
        var h = new Harness();
        const uint item = 0x70000A0Fu;
        h.Controller.IncrementBusyCount();

        Assert.True(h.Controller.PlaceWorldItemInBackpack(item));

        Assert.Empty(h.BackpackPlacements);
        Assert.False(h.Controller.TryGetPendingBackpackPlacement(item, out _));
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            h.SystemMessages);
    }

    [Fact]
    public void PendingPickupPreventsContainedUseRequest()
    {
        var h = new Harness();
        const uint pickup = 0x70000A10u;
        const uint contained = 0x50000A11u;
        h.AddContained(contained, item => item.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.PlaceWorldItemInBackpack(pickup));
        h.Now += 200;
        h.Controller.ActivateItem(contained);

        Assert.Empty(h.Uses);
        Assert.True(h.Controller.TryGetPendingBackpackPlacement(pickup, out _));
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            h.SystemMessages);
    }

    [Fact]
    public void PendingPickupPreventsPaperdollWieldRequest()
    {
        var h = new Harness();
        const uint pickup = 0x70000A12u;
        const uint helm = 0x50000A13u;
        h.AddContained(helm, item => item.ValidLocations = EquipMask.HeadWear);

        Assert.True(h.Controller.PlaceWorldItemInBackpack(pickup));
        Assert.False(h.Controller.WieldFromPaperdoll(helm, EquipMask.HeadWear));

        Assert.Empty(h.Wields);
        Assert.Equal(EquipMask.None, h.Objects.Get(helm)!.CurrentlyEquippedLocation);
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            h.SystemMessages);
    }

    [Fact]
    public void PendingPickupPreventsConfirmedUseRequest()
    {
        var h = new Harness();
        const uint pickup = 0x70000A14u;
        const uint item = 0x50000A15u;
        h.AddContained(item, contained => contained.Useability = ItemUseability.Contained);

        Assert.True(h.Controller.PlaceWorldItemInBackpack(pickup));
        Assert.False(h.Controller.ExecuteConfirmedUse(item));

        Assert.Empty(h.Uses);
        Assert.Equal(0, h.Controller.BusyCount);
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            h.SystemMessages);
    }

    [Fact]
    public void GlobalInventoryRequestRejectsPickupUntilMatchingMoveResponse()
    {
        var h = new Harness();
        const uint moving = 0x50000A16u;
        const uint unrelated = 0x50000A17u;
        const uint pickup = 0x70000A18u;
        h.AddContained(moving);
        h.AddContained(unrelated);

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            moving,
            static () => true));
        Assert.True(h.Controller.PlaceWorldItemInBackpack(pickup));

        Assert.Empty(h.BackpackPlacements);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(moving, pending.ItemId);
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            h.SystemMessages);

        h.Objects.ApplyConfirmedServerMove(unrelated, Pack, 0u, 0);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out _));

        h.Objects.ApplyConfirmedServerMove(moving, Pack, 0u, 0);
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
        Assert.True(h.Controller.PlaceWorldItemInBackpack(pickup));
        Assert.Equal(new[] { (pickup, Player, 0) }, h.BackpackPlacements);
    }

    [Fact]
    public void SplitRequestReleasesOnlyOnSourceStackResponse()
    {
        var h = new Harness();
        const uint source = 0x50000A19u;
        const uint unrelated = 0x50000A1Au;
        h.AddContained(source, item => item.StackSize = 10);
        h.AddContained(unrelated, item => item.StackSize = 10);

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.SplitToContainer,
            source,
            static () => true));
        h.Objects.UpdateStackSize(unrelated, 9, 0);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out _));

        h.Objects.UpdateStackSize(source, 9, 0);
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
    }

    [Fact]
    public void KeyboardPickup_AutoMergesWholeStackBeforeContainerPlacement()
    {
        var h = new Harness();
        const uint source = 0x70000B01u;
        const uint target = 0x50000B02u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = source,
            WeenieClassId = 77u,
            Name = "World stack",
            StackSize = 3,
            StackSizeMax = 10,
        });
        h.AddContained(target, item =>
        {
            item.WeenieClassId = 77u;
            item.StackSize = 5;
            item.StackSizeMax = 10;
        });
        var attempts = new List<(uint Source, uint Target)>();
        h.Controller.MergeAttempted += (from, into) => attempts.Add((from, into));

        Assert.True(h.Controller.PlaceWorldItemInBackpack(source));

        Assert.Equal(new[] { (source, target, 3u) }, h.Merges);
        Assert.Equal(new[] { (source, target) }, attempts);
        Assert.Empty(h.BackpackPlacements);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(InventoryRequestKind.Merge, pending.Kind);
        Assert.True(pending.Dispatched);
    }

    [Fact]
    public void KeyboardPickup_UsesSelectedSplitQuantityAndSearchesNestedPacks()
    {
        var h = new Harness();
        const uint nestedPack = 0x50000B10u;
        const uint source = 0x70000B11u;
        const uint target = 0x50000B12u;
        h.AddContained(nestedPack, item => item.Type = ItemType.Container);
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = source,
            WeenieClassId = 88u,
            StackSize = 10,
            StackSizeMax = 10,
        });
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = target,
            WeenieClassId = 88u,
            StackSize = 8,
            StackSizeMax = 10,
        });
        h.Objects.MoveItem(target, nestedPack, 0);
        h.SelectedObject = source;
        h.SplitQuantity.Reset(10u, 2u);

        Assert.True(h.Controller.PlaceWorldItemInBackpack(source));

        Assert.Equal(new[] { (source, target, 2u) }, h.Merges);
        Assert.Empty(h.BackpackPlacements);
    }

    [Fact]
    public void KeyboardPickup_SkipsPartialMergeTargetAndFallsBackToPlacement()
    {
        var h = new Harness();
        const uint source = 0x70000B20u;
        const uint partialTarget = 0x50000B21u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = source,
            WeenieClassId = 99u,
            StackSize = 3,
            StackSizeMax = 10,
        });
        h.AddContained(partialTarget, item =>
        {
            item.WeenieClassId = 99u;
            item.StackSize = 9;
            item.StackSizeMax = 10;
        });

        Assert.True(h.Controller.PlaceWorldItemInBackpack(source));

        Assert.Empty(h.Merges);
        Assert.Equal(new[] { (source, Player, 0) }, h.BackpackPlacements);
    }

    [Fact]
    public void TrySplitToContainerDispatchesTheExactSelectedQuantity()
    {
        var h = new Harness();
        const uint source = 0x50000A30u;
        h.AddContained(source, item => item.StackSize = 10);

        Assert.True(h.Controller.TrySplitToContainer(source, Pack, 3u, 2u));

        Assert.Equal(new[] { (source, Pack, 3u, 2u) }, h.SplitPuts);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(InventoryRequestKind.SplitToContainer, pending.Kind);
        Assert.Equal(source, pending.ItemId);
    }

    [Fact]
    public void TrySplitToContainerRejectsZeroAndWholeStackAmounts()
    {
        var h = new Harness();
        const uint source = 0x50000A31u;
        h.AddContained(source, item => item.StackSize = 10);

        Assert.False(h.Controller.TrySplitToContainer(source, Pack, 0u, 0u));
        Assert.False(h.Controller.TrySplitToContainer(source, Pack, 0u, 10u));

        Assert.Empty(h.SplitPuts);
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
    }

    [Fact]
    public void AutomationMoveUsesWholeOrExactPartialRetailRequest()
    {
        var whole = new Harness();
        const uint wholeItem = 0x50000A32u;
        whole.AddContained(wholeItem, item => item.StackSize = 10);

        Assert.True(whole.Controller.TryMoveItemForAutomation(
            wholeItem, Player, amount: 0u, placement: 7));
        Assert.Equal(new[] { (wholeItem, Player, 7) }, whole.Puts);
        Assert.True(whole.Controller.TryGetPendingInventoryRequest(out var put));
        Assert.Equal(InventoryRequestKind.PutInContainer, put.Kind);

        var partial = new Harness();
        const uint partialItem = 0x50000A33u;
        partial.AddContained(partialItem, item => item.StackSize = 10);

        Assert.True(partial.Controller.TryMoveItemForAutomation(
            partialItem, Player, amount: 2u, placement: 3));
        Assert.Equal(
            new[] { (partialItem, Player, 3u, 2u) },
            partial.SplitPuts);
        Assert.True(partial.Controller.TryGetPendingInventoryRequest(out var split));
        Assert.Equal(InventoryRequestKind.SplitToContainer, split.Kind);
    }

    [Fact]
    public void AutomationMergeUsesRetailPlannerAndSharedGate()
    {
        var h = new Harness();
        const uint source = 0x50000A34u;
        const uint target = 0x50000A35u;
        h.AddContained(source, item =>
        {
            item.WeenieClassId = 77u;
            item.StackSize = 8;
            item.StackSizeMax = 10;
        });
        h.AddContained(target, item =>
        {
            item.WeenieClassId = 77u;
            item.StackSize = 7;
            item.StackSizeMax = 10;
        });

        Assert.True(h.Controller.TryMergeItemsForAutomation(source, target));

        Assert.Equal(new[] { (source, target, 3u) }, h.Merges);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(InventoryRequestKind.Merge, pending.Kind);
        Assert.False(h.Controller.TryMoveItemForAutomation(source, Player));
        Assert.Single(h.Merges);
    }

    [Fact]
    public void AutomationDropAndGivePreserveExactStackAmounts()
    {
        var drop = new Harness();
        const uint dropItem = 0x50000A36u;
        drop.AddContained(dropItem, item => item.StackSize = 10);

        Assert.True(drop.Controller.TryDropItemForAutomation(dropItem, 2u));
        Assert.Equal(new[] { (dropItem, 2u) }, drop.SplitDrops);
        Assert.Empty(drop.Drops);

        var give = new Harness();
        const uint giveItem = 0x50000A37u;
        const uint recipient = 0x70000A38u;
        give.AddContained(giveItem, item => item.StackSize = 10);
        give.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = recipient,
            Name = "Recipient",
            Type = ItemType.Creature,
        });

        Assert.True(give.Controller.TryGiveItemForAutomation(
            giveItem, recipient, 4u));
        Assert.Equal(new[] { (recipient, giveItem, 4u) }, give.Gives);
    }

    [Fact]
    public void AutomationSalvageRequiresRetailToolAndSuitableOwnedItems()
    {
        var h = new Harness();
        const uint tool = 0x50000A40u;
        const uint source = 0x50000A41u;
        h.AddContained(tool, item => item.Type = ItemType.TinkeringTool);
        h.AddContained(source, item =>
        {
            item.MaterialType = 12u;
            item.Structure = 50;
        });

        Assert.True(h.Controller.TrySalvageItemsForAutomation(tool, [source]));
        Assert.Single(h.Salvages);
        Assert.Equal(tool, h.Salvages[0].ToolGuid);
        Assert.Equal(new[] { source }, h.Salvages[0].ItemGuids);

        h.Objects.Get(source)!.Structure = 100;
        Assert.False(h.Controller.TrySalvageItemsForAutomation(tool, [source]));
        Assert.Single(h.Salvages);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(9u)]
    [InlineData(56u)]
    [InlineData(65u)]
    [InlineData(72u)]
    public void TrySalvageItems_rejectsInvalidMaterialGroup(uint materialType)
    {
        var h = new Harness();
        const uint tool = 0x50000A42u;
        const uint source = 0x50000A43u;
        h.AddContained(tool, item => item.Type = ItemType.TinkeringTool);
        h.AddContained(source, item =>
        {
            item.MaterialType = materialType;
            item.Structure = 50;
        });

        Assert.False(h.Controller.TrySalvageItems(tool, [source]));
        Assert.Empty(h.Salvages);
    }

    [Fact]
    public void TrySalvageItems_sendsOwnedSuitableItem()
    {
        var h = new Harness();
        const uint tool = 0x50000A44u;
        const uint source = 0x50000A45u;
        h.AddContained(tool, item => item.Type = ItemType.TinkeringTool);
        h.AddContained(source, item =>
        {
            item.MaterialType = 12u;
            item.Structure = 50;
        });

        Assert.True(h.Controller.TrySalvageItems(tool, [source]));
        Assert.Single(h.Salvages);
        Assert.Equal(tool, h.Salvages[0].ToolGuid);
        Assert.Equal(new[] { source }, h.Salvages[0].ItemGuids);
    }

    [Fact]
    public void MatchingInventoryFailureReleasesGlobalRequest()
    {
        var h = new Harness();
        const uint source = 0x50000A1Bu;
        h.AddContained(source);

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            source,
            static () => true));

        h.Objects.RejectMove(source, weenieError: 0x29u);

        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
        Assert.True(h.Controller.CanMakeInventoryRequest);
    }

    [Fact]
    public void ProvisionalInventoryOwnerRejectsReentrantSecondDispatch()
    {
        var h = new Harness();
        const uint first = 0x50000A1Cu;
        const uint second = 0x50000A1Du;
        h.AddContained(first);
        h.AddContained(second);
        bool secondDispatched = true;

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            first,
            () =>
            {
                secondDispatched = h.Controller.TryDispatchInventoryRequest(
                    InventoryRequestKind.PutInContainer,
                    second,
                    static () => true);
                return true;
            }));

        Assert.False(secondDispatched);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(first, pending.ItemId);
        Assert.True(pending.Dispatched);
        Assert.Equal(
            new[] { ItemInteractionController.InventoryRequestBusyMessage },
            h.SystemMessages);
    }

    [Fact]
    public void SynchronousMatchingResponseDoesNotResurrectProvisionalOwner()
    {
        var h = new Harness();
        const uint item = 0x50000A1Eu;
        h.AddContained(item);

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            item,
            () => h.Objects.ApplyConfirmedServerMove(item, Player, 0u, 0)));

        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
        Assert.True(h.Controller.CanMakeInventoryRequest);
    }

    [Fact]
    public void OptimisticMoveKeepsGlobalOwnerUntilAuthoritativeResponse()
    {
        var h = new Harness();
        const uint item = 0x50000A20u;
        const uint second = 0x50000A21u;
        h.AddContained(item);
        h.AddContained(second);

        Assert.True(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            item,
            () => h.Objects.MoveItemOptimistic(item, Player, 0)));

        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(item, pending.ItemId);
        Assert.True(pending.Dispatched);
        Assert.False(h.Controller.TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            second,
            static () => true));

        Assert.True(h.Objects.ApplyConfirmedServerMove(item, Player, 0u, 0));
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
    }

    [Fact]
    public void AuthoritativeResponseReleasesGlobalOwnerBeforePublishingPlacementResolution()
    {
        var h = new Harness();
        const uint first = 0x70000A22u;
        const uint second = 0x50000A23u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = first,
            Name = "Loot",
            Type = ItemType.Misc,
        });
        h.AddContained(second);
        bool secondDispatched = false;
        h.Controller.PendingBackpackPlacementResolved += _ =>
            secondDispatched = h.Controller.TryDispatchInventoryRequest(
                InventoryRequestKind.PutInContainer,
                second,
                static () => true);

        Assert.True(h.Controller.TryDispatchPendingBackpackPlacement(
            first,
            Player,
            0,
            InventoryRequestKind.Pickup,
            static () => true));

        Assert.True(h.Objects.ApplyConfirmedServerMove(first, Player, 0u, 0));

        Assert.True(secondDispatched);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(second, pending.ItemId);
        Assert.True(pending.Dispatched);
    }

    [Fact]
    public void FailedMoveReleasesOldOwnerOnceAndPreservesReentrantSameItemRequest()
    {
        var h = new Harness();
        const uint item = 0x50000A24u;
        h.AddContained(item);
        bool replacementDispatched = false;
        h.Controller.PendingBackpackPlacementResolved += _ =>
            replacementDispatched = h.Controller.TryDispatchInventoryRequest(
                InventoryRequestKind.PutInContainer,
                item,
                static () => true);

        Assert.True(h.Controller.TryDispatchPendingBackpackPlacement(
            item,
            Player,
            0,
            InventoryRequestKind.PutInContainer,
            () => h.Objects.MoveItemOptimistic(item, Player, 0)));

        Assert.True(h.Objects.RejectMove(item, weenieError: 0x29u));

        Assert.True(replacementDispatched);
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(item, pending.ItemId);
        Assert.True(pending.Dispatched);
    }

    [Fact]
    public void ResponseClearsGlobalAndLocalStateBeforeReadinessNotification()
    {
        var h = new Harness();
        const uint first = 0x70000A25u;
        const uint second = 0x70000A26u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = first,
            Name = "First loot",
            Type = ItemType.Misc,
        });
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = second,
            Name = "Second loot",
            Type = ItemType.Misc,
        });
        Assert.True(h.Controller.TryDispatchPendingBackpackPlacement(
            first,
            Player,
            0,
            InventoryRequestKind.Pickup,
            static () => true));

        var eventOrder = new List<string>();
        h.Controller.PendingBackpackPlacementResolved += placement =>
        {
            if (placement.ItemId == first)
                eventOrder.Add("resolved-first");
        };
        h.Controller.PendingBackpackPlacementRequested += placement =>
        {
            if (placement.ItemId == second)
                eventOrder.Add("requested-second");
        };
        bool attempted = false;
        bool secondDispatched = false;
        h.Controller.StateChanged += () =>
        {
            if (attempted || !h.Controller.CanMakeInventoryRequest)
                return;
            attempted = true;
            eventOrder.Add("ready");
            secondDispatched = h.Controller.TryDispatchPendingBackpackPlacement(
                second,
                Player,
                0,
                InventoryRequestKind.Pickup,
                static () => true);
        };

        Assert.True(h.Objects.ApplyConfirmedServerMove(first, Player, 0u, 0));

        Assert.True(attempted);
        Assert.True(secondDispatched);
        Assert.True(h.Controller.TryGetPendingBackpackPlacement(second, out _));
        Assert.True(h.Controller.TryGetPendingInventoryRequest(out var pending));
        Assert.Equal(second, pending.ItemId);
        Assert.True(pending.Dispatched);
        Assert.Equal(
            new[] { "resolved-first", "ready", "requested-second" },
            eventOrder);
    }

    [Fact]
    public void ReentrantPendingPublicationCancellationPreventsWireDispatch()
    {
        var h = new Harness();
        const uint item = 0x70000A1Fu;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Name = "Loot",
            Type = ItemType.Misc,
        });
        bool wireDispatched = false;
        h.Controller.PendingBackpackPlacementRequested += pending =>
            h.Controller.CancelPendingBackpackPlacement(pending.ItemId, pending.Token);

        Assert.False(h.Controller.TryDispatchPendingBackpackPlacement(
            item,
            Player,
            0,
            InventoryRequestKind.Pickup,
            () =>
            {
                wireDispatched = true;
                return true;
            }));

        Assert.False(wireDispatched);
        Assert.False(h.Controller.TryGetPendingBackpackPlacement(item, out _));
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
    }

    [Fact]
    public void ThrowingReservedDispatchWithdrawsBothOwnersBeforeRethrow()
    {
        var h = new Harness();
        const uint item = 0x70000A27u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Name = "Loot",
            Type = ItemType.Misc,
        });
        var cancelled = new List<PendingBackpackPlacement>();
        h.Controller.PendingBackpackPlacementCancelled += cancelled.Add;

        Assert.Throws<InvalidOperationException>(() =>
            h.Controller.TryDispatchPendingBackpackPlacement(
                item,
                Player,
                0,
                InventoryRequestKind.Pickup,
                static () => throw new InvalidOperationException("transport")));

        Assert.False(h.Controller.TryGetPendingBackpackPlacement(item, out _));
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
        Assert.Single(cancelled);
    }

    [Fact]
    public void ThrowingPendingProjectionObserverRollsBackEveryOwner()
    {
        var h = new Harness();
        const uint item = 0x70000A28u;
        h.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = item,
            Name = "Loot",
            Type = ItemType.Misc,
        });
        bool firstObserved = false;
        bool cancelled = false;
        h.Controller.PendingBackpackPlacementRequested += _ =>
            firstObserved = true;
        h.Controller.PendingBackpackPlacementRequested += _ =>
            throw new InvalidOperationException("projection");
        h.Controller.PendingBackpackPlacementCancelled += _ =>
            cancelled = true;

        Assert.Throws<AggregateException>(() =>
            h.Controller.TryDispatchPendingBackpackPlacement(
                item,
                Player,
                0,
                InventoryRequestKind.Pickup,
                static () => true));

        Assert.True(firstObserved);
        Assert.True(cancelled);
        Assert.False(h.Controller.TryGetPendingBackpackPlacement(item, out _));
        Assert.False(h.Controller.TryGetPendingInventoryRequest(out _));
    }

    [Fact]
    public void ResetSession_ClearsTargetBusyThrottleAndConsumedClickState()
    {
        var h = new Harness();
        const uint kit = 0x50000A0Cu;
        h.AddContained(kit, item => item.Useability = HealthKitUseability);
        Assert.True(h.Controller.ActivateItem(kit));
        h.Controller.IncrementBusyCount();

        h.Controller.ResetSession();

        Assert.False(h.Controller.IsAnyTargetModeActive);
        Assert.Equal(0, h.Controller.BusyCount);
        Assert.Equal(InteractionModeKind.None, h.Controller.InteractionState.Current.Kind);
    }


    [Fact]
    public void TryBuy_Succeeds_SendsBuyAndTakesTheSharedUseReservation()
    {
        var h = new Harness();

        bool result = h.Controller.TryBuy(
            vendorGuid: 0x40001000u,
            itemGuid: 0x50002000u,
            amount: 1,
            alternateCurrencyId: 0u);

        Assert.True(result);
        Assert.Equal(
            new[] { (0x40001000u, 0x50002000u, 1, 0u) },
            h.Buys);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void TryBuy_StackedQuantity_ForwardsTheExactAmount()
    {
        var h = new Harness();

        Assert.True(h.Controller.TryBuy(0x40001000u, 0x50002001u, 25, 0u));

        Assert.Equal(25, h.Buys.Single().Amount);
    }

    [Fact]
    public void TryBuy_AlternateCurrencyVendor_ForwardsTheCurrencyWcid()
    {
        var h = new Harness();

        Assert.True(h.Controller.TryBuy(0x40001000u, 0x50002000u, 1, 0x12345678u));

        Assert.Equal(0x12345678u, h.Buys.Single().AlternateCurrencyId);
    }

    [Fact]
    public void TryBuy_WhileAnotherRequestIsBusy_IsRejectedAndSendsNothing()
    {
        var h = new Harness();
        h.Controller.IncrementBusyCount(); // simulates any other in-flight request

        bool result = h.Controller.TryBuy(0x40001000u, 0x50002000u, 1, 0u);

        Assert.False(result);
        Assert.Empty(h.Buys);
        Assert.Equal(1, h.Controller.BusyCount); // unchanged -- no second reservation taken
    }

    [Fact]
    public void TryBuy_ASecondBuyWhileTheFirstIsInFlight_IsRejected()
    {
        var h = new Harness();
        Assert.True(h.Controller.TryBuy(0x40001000u, 0x50002000u, 1, 0u));

        bool second = h.Controller.TryBuy(0x40001000u, 0x50002001u, 1, 0u);

        Assert.False(second);
        Assert.Single(h.Buys);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Theory]
    [InlineData(0u, 0x50002000u, 1)]
    [InlineData(0x40001000u, 0u, 1)]
    [InlineData(0x40001000u, 0x50002000u, 0)]
    [InlineData(0x40001000u, 0x50002000u, -1)]
    public void TryBuy_InvalidArguments_IsRejectedWithoutTakingAReservation(
        uint vendorGuid, uint itemGuid, int amount)
    {
        var h = new Harness();

        bool result = h.Controller.TryBuy(vendorGuid, itemGuid, amount, 0u);

        Assert.False(result);
        Assert.Empty(h.Buys);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void TryBuy_CompleteUse_ReleasesTheReservationAndReenablesFurtherRequests()
    {
        var h = new Harness();
        Assert.True(h.Controller.TryBuy(0x40001000u, 0x50002000u, 1, 0u));
        Assert.Equal(1, h.Controller.BusyCount);

        h.Controller.CompleteUse(0);

        Assert.Equal(0, h.Controller.BusyCount);
        // The gate is free again -- a second Buy can now proceed.
        Assert.True(h.Controller.TryBuy(0x40001000u, 0x50002001u, 1, 0u));
        Assert.Equal(2, h.Buys.Count);
    }

    [Fact]
    public void TryBuy_FailedUseDone_AlsoReleasesTheReservation()
    {
        // A.2's failure paths all still end in exactly one SendUseDoneEvent
        // -- success or failure, the reservation resolves the same way.
        var h = new Harness();
        Assert.True(h.Controller.TryBuy(0x40001000u, 0x50002000u, 1, 0u));

        h.Controller.CompleteUse(0x0009u); // an arbitrary nonzero WeenieError

        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void TryBuy_NoSessionToSendOn_ReleasesTheReservation_AndASubsequentBuyWorks()
    {
        var h = new Harness();
        h.SendBuySucceeds = false;

        bool result = h.Controller.TryBuy(0x40001000u, 0x50002000u, 1, 0u);

        Assert.False(result);
        Assert.Empty(h.Buys);
        Assert.Equal(0, h.Controller.BusyCount);

        // A subsequent buy, once a session IS available, proceeds normally.
        h.SendBuySucceeds = true;
        bool second = h.Controller.TryBuy(0x40001000u, 0x50002001u, 1, 0u);

        Assert.True(second);
        Assert.Single(h.Buys);
        Assert.Equal(1, h.Controller.BusyCount);
    }


    [Fact]
    public void TryBuyAll_Succeeds_SendsTheBatchAndTakesTheSharedUseReservation()
    {
        var h = new Harness();
        var items = new (int Amount, uint ItemGuid)[]
        {
            (1, 0x50002000u),
            (25, 0x50002001u),
        };

        bool result = h.Controller.TryBuyAll(0x40001000u, items, alternateCurrencyId: 0u);

        Assert.True(result);
        (uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> sent, uint currency) = Assert.Single(h.BuyAlls);
        Assert.Equal(0x40001000u, vendorGuid);
        Assert.Equal(items, sent);
        Assert.Equal(0u, currency);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void TryBuyAll_RidesTheSameOneRequestAtATimeGateAsOrdinaryUse()
    {
        var h = new Harness();
        h.Controller.IncrementBusyCount();

        bool result = h.Controller.TryBuyAll(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) }, 0u);

        Assert.False(result);
        Assert.Empty(h.BuyAlls);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40001000u)]
    public void TryBuyAll_ZeroVendorOrEmptyList_IsRejectedWithoutTakingAReservation(uint vendorGuid)
    {
        var h = new Harness();
        var items = vendorGuid == 0u
            ? new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) }
            : Array.Empty<(int Amount, uint ItemGuid)>();

        bool result = h.Controller.TryBuyAll(vendorGuid, items, 0u);

        Assert.False(result);
        Assert.Empty(h.BuyAlls);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void TryBuyAll_CompleteUse_ReleasesTheReservation()
    {
        var h = new Harness();
        Assert.True(h.Controller.TryBuyAll(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) }, 0u));
        Assert.Equal(1, h.Controller.BusyCount);

        h.Controller.CompleteUse(0);

        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void TryBuyAll_NoSessionToSendOn_ReleasesTheReservation()
    {
        var h = new Harness();
        h.SendBuyAllSucceeds = false;

        bool result = h.Controller.TryBuyAll(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50002000u) }, 0u);

        Assert.False(result);
        Assert.Empty(h.BuyAlls);
        Assert.Equal(0, h.Controller.BusyCount);
    }


    [Fact]
    public void TrySell_Succeeds_SendsTheBatchAndTakesTheSharedUseReservation()
    {
        var h = new Harness();
        var items = new (int Amount, uint ItemGuid)[] { (1, 0x50003000u) };

        bool result = h.Controller.TrySell(0x40001000u, items);

        Assert.True(result);
        (uint vendorGuid, IReadOnlyList<(int Amount, uint ItemGuid)> sent) = Assert.Single(h.Sells);
        Assert.Equal(0x40001000u, vendorGuid);
        Assert.Equal(items, sent);
        Assert.Equal(1, h.Controller.BusyCount);
    }

    [Fact]
    public void TrySell_RidesTheSameOneRequestAtATimeGateAsOrdinaryUse()
    {
        var h = new Harness();
        h.Controller.IncrementBusyCount();

        bool result = h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003000u) });

        Assert.False(result);
        Assert.Empty(h.Sells);
    }

    [Fact]
    public void TrySell_ASecondSellWhileTheFirstIsInFlight_IsRejected()
    {
        var h = new Harness();
        Assert.True(h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003000u) }));

        bool second = h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003001u) });

        Assert.False(second);
        Assert.Single(h.Sells);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40001000u)]
    public void TrySell_ZeroVendorOrEmptyList_IsRejectedWithoutTakingAReservation(uint vendorGuid)
    {
        var h = new Harness();
        var items = vendorGuid == 0u
            ? new (int Amount, uint ItemGuid)[] { (1, 0x50003000u) }
            : Array.Empty<(int Amount, uint ItemGuid)>();

        bool result = h.Controller.TrySell(vendorGuid, items);

        Assert.False(result);
        Assert.Empty(h.Sells);
        Assert.Equal(0, h.Controller.BusyCount);
    }

    [Fact]
    public void TrySell_CompleteUse_ReleasesTheReservationAndReenablesFurtherRequests()
    {
        var h = new Harness();
        Assert.True(h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003000u) }));
        Assert.Equal(1, h.Controller.BusyCount);

        h.Controller.CompleteUse(0);

        Assert.Equal(0, h.Controller.BusyCount);
        Assert.True(h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003001u) }));
        Assert.Equal(2, h.Sells.Count);
    }

    [Fact]
    public void TrySell_NoSessionToSendOn_ReleasesTheReservation_AndASubsequentSellWorks()
    {
        var h = new Harness();
        h.SendSellSucceeds = false;

        bool result = h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003000u) });

        Assert.False(result);
        Assert.Empty(h.Sells);
        Assert.Equal(0, h.Controller.BusyCount);

        h.SendSellSucceeds = true;
        bool second = h.Controller.TrySell(
            0x40001000u, new (int Amount, uint ItemGuid)[] { (1, 0x50003001u) });

        Assert.True(second);
        Assert.Single(h.Sells);
        Assert.Equal(1, h.Controller.BusyCount);
    }
}
