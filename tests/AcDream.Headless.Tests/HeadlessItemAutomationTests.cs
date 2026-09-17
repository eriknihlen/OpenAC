using AcDream.Core.Items;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

public sealed class HeadlessItemAutomationTests
{
    private const uint Player = 0x50000001u;
    private const uint Item = 0x50000A01u;
    private const uint Container = 0x50000B01u;
    private const uint Source = 0x50000A03u;
    private const uint Target = 0x50000A04u;
    private const uint TargetedUseability =
        ((ItemUseability.Remote | ItemUseability.Self) << 16) | ItemUseability.Contained;

    [Fact]
    public void TryUse_OwnedUseableItemSends()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);

        Assert.True(h.Automation.TryUse(Item));

        Assert.Equal(new[] { Item }, h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_TargetedItemDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_TradeStateItemDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained, tradeState: 1);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_VolatileRareDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(
            Item,
            ItemUseability.Contained,
            flags: PublicWeenieFlags.VolatileRare);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_NonUseableItemDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.No);

        Assert.False(h.Automation.TryUse(Item));

        Assert.Empty(h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_SecondUseInsideTheThrottleDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);

        Assert.True(h.Automation.TryUse(Item));
        Assert.False(h.Automation.TryUse(Item));

        Assert.Equal(new[] { Item }, h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_SecondUseAfterTheThrottleElapsesSends()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);

        Assert.True(h.Automation.TryUse(Item));
        h.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        _ = h.Runtime.Clock.Advance(0.25);
        Assert.True(h.Automation.TryUse(Item));

        Assert.Equal(new[] { Item, Item }, h.Transport.UseCalls);
    }

    [Fact]
    public void TryUse_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);
        h.Transport.SendUseSucceeds = false;

        Assert.False(h.Automation.TryUse(Item));

        Assert.Equal(0, h.Runtime.InventoryOwner.Transactions.BusyCount);
        Assert.True(h.Runtime.InventoryOwner.Transactions.CanBeginRequest);
    }

    [Fact]
    public void TryUse_ReleasesTheReservationAndRethrowsWhenDispatchThrows()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);
        h.Transport.ThrowOnSend = true;

        Assert.Throws<InvalidOperationException>(() => h.Automation.TryUse(Item));

        Assert.Equal(0, h.Runtime.InventoryOwner.Transactions.BusyCount);
        Assert.True(h.Runtime.InventoryOwner.Transactions.CanBeginRequest);
    }

    [Fact]
    public void TryApply_TargetedItemAppliedToOwnedTargetSends()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability, targetType: (uint)ItemType.Misc);
        h.AddOwnedItem(Target, ItemUseability.Undef);

        Assert.True(h.Automation.TryApply(Item, Target));

        Assert.Equal(new[] { (Item, Target) }, h.UsesWithTarget);
        Assert.Equal(1, h.Runtime.InventoryOwner.Transactions.BusyCount);
        Assert.True(h.Runtime.ActionOwner.Transactions.CaptureOwnership().AwaitingItemUseCompletion);
    }

    [Fact]
    public void TryApply_CompletionClearsAwaitingOnSuccessAndOnAWeenieError()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability, targetType: (uint)ItemType.Misc);
        h.AddOwnedItem(Target, ItemUseability.Undef);
        Assert.True(h.Automation.TryApply(Item, Target));

        h.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        Assert.False(h.Runtime.ActionOwner.Transactions.CaptureOwnership().AwaitingItemUseCompletion);

        _ = h.Runtime.Clock.Advance(0.25);
        Assert.True(h.Automation.TryApply(Item, Target));
        h.Runtime.ActionOwner.Transactions.CompleteUse(29u);
        Assert.False(h.Runtime.ActionOwner.Transactions.CaptureOwnership().AwaitingItemUseCompletion);
    }

    [Fact]
    public void TryApply_CompletionReturnsBusyCountToZeroOnSuccessAndOnAWeenieError()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability, targetType: (uint)ItemType.Misc);
        h.AddOwnedItem(Target, ItemUseability.Undef);
        Assert.True(h.Automation.TryApply(Item, Target));

        h.Runtime.ActionOwner.Transactions.CompleteUse(0u);
        Assert.Equal(0, h.Runtime.InventoryOwner.Transactions.BusyCount);

        _ = h.Runtime.Clock.Advance(0.25);
        Assert.True(h.Automation.TryApply(Item, Target));
        h.Runtime.ActionOwner.Transactions.CompleteUse(29u);
        Assert.Equal(0, h.Runtime.InventoryOwner.Transactions.BusyCount);
    }

    [Fact]
    public void TryApply_UntargetedItemIsRejectedByPolicyBeforeAnySend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);
        h.AddOwnedItem(Target, ItemUseability.Undef);

        Assert.False(h.Automation.TryApply(Item, Target));

        Assert.Empty(h.UsesWithTarget);
    }

    [Fact]
    public void TryApply_RefusesWithoutDispatchingWhenTheRouteGuardFails()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability, targetType: (uint)ItemType.Misc);
        h.AddOwnedItem(Target, ItemUseability.Undef);
        h.Transport.IsInWorld = false;

        Assert.False(h.Automation.TryApply(Item, Target));

        Assert.Empty(h.UsesWithTarget);
        Assert.False(h.Runtime.ActionOwner.Transactions.CaptureOwnership().AwaitingItemUseCompletion);
    }

    [Fact]
    public void TryApply_RefusesWhileBusy()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability, targetType: (uint)ItemType.Misc);
        h.AddOwnedItem(Target, ItemUseability.Undef);
        h.Runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.False(h.Automation.TryApply(Item, Target));

        Assert.Empty(h.UsesWithTarget);
    }

    [Fact]
    public void TryApply_SecondApplyInsideTheThrottleDoesNotSend()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, TargetedUseability, targetType: (uint)ItemType.Misc);
        h.AddOwnedItem(Target, ItemUseability.Undef);

        Assert.True(h.Automation.TryApply(Item, Target));
        Assert.False(h.Automation.TryApply(Item, Target));

        Assert.Equal(new[] { (Item, Target) }, h.UsesWithTarget);
    }

    [Fact]
    public void TryDrop_FullStackSendsDrop()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);

        Assert.True(h.Automation.TryDrop(Item, amount: 0u));

        Assert.Equal(new[] { Item }, h.Drops);
        Assert.Empty(h.SplitsToWorld);
    }

    [Fact]
    public void TryDrop_PartialStackSendsSplitTo3DWithTheRequestedAmount()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);

        Assert.True(h.Automation.TryDrop(Item, amount: 2u));

        Assert.Equal((Item, 2u), Assert.Single(h.SplitsToWorld));
        Assert.Empty(h.Drops);
    }

    [Fact]
    public void TryDrop_AmountEqualToAStackGreaterThanOneSendsDropNotSplit()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);

        Assert.True(h.Automation.TryDrop(Item, amount: 5u));

        Assert.Equal(new[] { Item }, h.Drops);
        Assert.Empty(h.SplitsToWorld);
    }

    [Fact]
    public void TryDrop_AmountGreaterThanTheStackSendsNothing()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);

        Assert.False(h.Automation.TryDrop(Item, amount: 6u));

        Assert.Empty(h.Drops);
        Assert.Empty(h.SplitsToWorld);
    }

    [Fact]
    public void TryGive_SendsGiveObject()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);

        Assert.True(h.Automation.TryGive(Item, Target, amount: 0u));

        Assert.Equal((Target, Item, 5u), Assert.Single(h.Gives));
    }

    [Fact]
    public void TryGive_AmountEqualToAStackGreaterThanOneSendsTheFullAmount()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);

        Assert.True(h.Automation.TryGive(Item, Target, amount: 5u));

        Assert.Equal((Target, Item, 5u), Assert.Single(h.Gives));
    }

    [Fact]
    public void TryGive_AmountGreaterThanTheStackSendsNothing()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);

        Assert.False(h.Automation.TryGive(Item, Target, amount: 6u));

        Assert.Empty(h.Gives);
    }

    [Fact]
    public void TryGive_ServerRefusalWithoutAGuidClearsThePendingRequest()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        Assert.True(h.Automation.TryGive(Item, Target, amount: 0u));
        Assert.True(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);

        h.Runtime.InventoryOwner.Objects.RejectMove(0u, 0x0426u);

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryGive_ItemLeavingTheInventoryClearsThePendingRequest()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        Assert.True(h.Automation.TryGive(Item, Target, amount: 0u));

        h.Runtime.InventoryOwner.Objects.Remove(Item);

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryDropAndTryGive_RefuseWhileBusy()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.Runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.False(h.Automation.TryDrop(Item, amount: 0u));
        Assert.False(h.Automation.TryGive(Item, Target, amount: 0u));

        Assert.Empty(h.Drops);
        Assert.Empty(h.Gives);
    }

    [Fact]
    public void TryDrop_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.DropResult = false;

        Assert.False(h.Automation.TryDrop(Item, amount: 0u));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryGive_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.GiveResult = false;

        Assert.False(h.Automation.TryGive(Item, Target, amount: 0u));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryMove_FullStackSendsPutInContainer()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);

        Assert.True(h.Automation.TryMove(Item, Container, amount: 0u, placement: 3));

        Assert.Equal((Item, Container, 3), Assert.Single(h.Puts));
        Assert.Empty(h.Splits);
    }

    [Fact]
    public void TryMove_PartialStackSendsSplitWithAmountAndClampedPlacement()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);
        h.AddOwnedContainer(Container);

        Assert.True(h.Automation.TryMove(Item, Container, amount: 2u, placement: -1));

        Assert.Equal((Item, Container, 0u, 2u), Assert.Single(h.Splits));
        Assert.Empty(h.Puts);
    }

    [Fact]
    public void TryMerge_SendsThePlannersAmount()
    {
        var h = new Harness();
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);

        Assert.True(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.Equal((Source, Target, 3u), Assert.Single(h.Merges));
    }

    [Fact]
    public void TryMoveAndTryMerge_RefuseWhileBusy()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        h.Runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.Empty(h.Puts);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public void TryMoveAndTryMerge_RefuseWhileARequestIsPending()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        Assert.True(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        h.Puts.Clear();

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.Empty(h.Puts);
        Assert.Empty(h.Merges);
    }

    [Fact]
    public void TryMove_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 1, stackSizeMax: 1);
        h.AddOwnedContainer(Container);
        h.PutResult = false;

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryMove_SplitRefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Undef, stackSize: 5, stackSizeMax: 10);
        h.AddOwnedContainer(Container);
        h.SplitResult = false;

        Assert.False(h.Automation.TryMove(Item, Container, amount: 2u, placement: 0));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryMerge_RefusesWithoutDispatchingWhenTheSenderReturnsFalse()
    {
        var h = new Harness();
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        h.MergeResult = false;

        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));

        Assert.False(h.Runtime.InventoryOwner.Transactions.HasPendingRequest);
    }

    [Fact]
    public void TryUseMoveMergeAndEquip_RefuseWhileAWieldSwitchOutlivesItsFirstRequest()
    {
        var h = new Harness();
        h.AddOwnedItem(Item, ItemUseability.Contained);
        h.AddOwnedContainer(Container);
        h.AddOwnedItem(
            Source, ItemUseability.Undef, stackSize: 3, stackSizeMax: 10, weenieClassId: 7u);
        h.AddOwnedItem(
            Target, ItemUseability.Undef, stackSize: 2, stackSizeMax: 10, weenieClassId: 7u);
        h.BeginWieldSwitchThatOutlivesItsFirstRequest();
        Assert.True(h.Runtime.InventoryOwner.Transactions.CanBeginRequest);
        Assert.True(h.AutoWield!.IsBusy);

        Assert.False(h.Automation.TryMove(Item, Container, amount: 0u, placement: 0));
        Assert.False(h.Automation.TryMerge(Source, Target, amount: 0u));
        Assert.False(h.Automation.TryUse(Item));
        Assert.False(h.Automation.TryEquip(0u, (uint)EquipMask.Held));

        Assert.Empty(h.Puts);
        Assert.Empty(h.Merges);
        Assert.Empty(h.Transport.UseCalls);
    }

    private sealed class FakeTransport : IRuntimeInteractionTransport
    {
        internal bool SendUseSucceeds { get; set; } = true;
        internal bool ThrowOnSend { get; set; }
        internal List<uint> UseCalls { get; } = [];

        public bool IsInWorld { get; set; } = true;

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            UseCalls.Add(serverGuid);
            if (ThrowOnSend)
                throw new InvalidOperationException("send failed");
            sequence = SendUseSucceeds ? 1u : 0u;
            return SendUseSucceeds;
        }

        public bool TrySendPickup(
            uint itemGuid, uint destinationContainerId, int placement, out uint sequence)
        {
            sequence = 0u;
            return false;
        }
    }

    private sealed class Harness
    {
        private const uint WieldSwitchBlocker = 0x50000D01u;
        private const uint WieldSwitchRequested = 0x50000D02u;
        private const uint WieldSwitchSubPack = 0x50000D03u;

        internal readonly GameRuntime Runtime;
        internal readonly FakeTransport Transport = new();
        internal readonly List<(uint Item, uint Container, int Placement)> Puts = [];
        internal readonly List<(uint Item, uint Container, uint Placement, uint Amount)> Splits = [];
        internal readonly List<(uint Source, uint Target, uint Amount)> Merges = [];
        internal readonly List<(uint Source, uint Target)> UsesWithTarget = [];
        internal readonly List<uint> Drops = [];
        internal readonly List<(uint Item, uint Amount)> SplitsToWorld = [];
        internal readonly List<(uint Target, uint Item, uint Amount)> Gives = [];
        internal bool PutResult = true;
        internal bool SplitResult = true;
        internal bool MergeResult = true;
        internal bool UseWithTargetResult = true;
        internal bool DropResult = true;
        internal bool SplitToWorldResult = true;
        internal bool GiveResult = true;
        internal readonly HeadlessItemAutomation Automation;
        internal readonly AutoWieldController? AutoWield;

        internal Harness()
        {
            Runtime = NewRuntime();
            Runtime.PlayerIdentity.ServerGuid = Player;
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Type = ItemType.Creature,
            });
            AutoWield = new AutoWieldController(
                Runtime.InventoryOwner.Objects,
                () => Runtime.PlayerIdentity.ServerGuid,
                sendWield: (_, _) => true,
                sendPutItemInContainer: (item, container, placement) => true,
                transactions: Runtime.InventoryOwner.Transactions);
            Automation = new HeadlessItemAutomation(
                Runtime,
                Transport,
                (item, container, placement) =>
                {
                    Puts.Add((item, container, placement));
                    return PutResult;
                },
                (item, container, placement, amount) =>
                {
                    Splits.Add((item, container, placement, amount));
                    return SplitResult;
                },
                (source, target, amount) =>
                {
                    Merges.Add((source, target, amount));
                    return MergeResult;
                },
                (source, target) =>
                {
                    UsesWithTarget.Add((source, target));
                    return UseWithTargetResult;
                },
                item =>
                {
                    Drops.Add(item);
                    return DropResult;
                },
                (item, amount) =>
                {
                    SplitsToWorld.Add((item, amount));
                    return SplitToWorldResult;
                },
                (target, item, amount) =>
                {
                    Gives.Add((target, item, amount));
                    return GiveResult;
                },
                isComponentPack: null,
                autoWield: AutoWield);
        }

        // The blocker's confirmed move lands it in a sub-pack rather than the
        // root inventory the switch expects, so the switch survives the
        // request that freed the transaction.
        internal void BeginWieldSwitchThatOutlivesItsFirstRequest()
        {
            AddOwnedContainer(WieldSwitchSubPack);
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = WieldSwitchBlocker,
                Type = ItemType.MeleeWeapon,
                ValidLocations = EquipMask.MeleeWeapon,
            });
            Runtime.InventoryOwner.Objects.MoveItem(
                WieldSwitchBlocker, Player, newSlot: -1, newEquipLocation: EquipMask.MeleeWeapon);
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = WieldSwitchRequested,
                Type = ItemType.MeleeWeapon,
                ContainerId = Player,
                ValidLocations = EquipMask.MeleeWeapon,
            });

            Assert.True(AutoWield!.TryWield(
                Runtime.InventoryOwner.Objects.Get(WieldSwitchRequested)!,
                EquipMask.MeleeWeapon));
            Runtime.InventoryOwner.Objects.ApplyConfirmedServerMove(
                WieldSwitchBlocker, WieldSwitchSubPack, newWielderId: 0u);
        }

        internal void AddOwnedItem(
            uint id,
            uint useability,
            int stackSize = 1,
            int stackSizeMax = 1,
            uint weenieClassId = 0u,
            int tradeState = 0,
            PublicWeenieFlags flags = PublicWeenieFlags.None,
            uint targetType = 0u) =>
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Misc,
                ContainerId = Player,
                WeenieClassId = weenieClassId,
                Useability = useability,
                TargetType = targetType,
                StackSize = stackSize,
                StackSizeMax = stackSizeMax,
                TradeState = tradeState,
                PublicWeenieBitfield = (uint)flags,
            });

        internal void AddOwnedContainer(uint id) =>
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Container,
                ContainerId = Player,
                ItemsCapacity = 24,
            });

        private static GameRuntime NewRuntime()
        {
            var gameplay = new HeadlessGameplayOperations();
            var runtime = new GameRuntime(new GameRuntimeDependencies(
                gameplay,
                gameplay,
                gameplay,
                gameplay));
            gameplay.Bind(runtime, catalog: null, () => "account");
            return runtime;
        }
    }
}
