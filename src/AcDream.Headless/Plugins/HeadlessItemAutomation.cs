using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessItemAutomation
{
    private readonly GameRuntime _runtime;
    private readonly IRuntimeInteractionTransport _transport;
    private readonly Func<uint, uint, int, bool> _sendPutItemInContainer;
    private readonly Func<uint, uint, uint, uint, bool> _sendStackableSplitToContainer;
    private readonly Func<uint, uint, uint, bool> _sendStackableMerge;
    private readonly Func<uint, uint, bool> _sendUseWithTarget;
    private readonly Func<uint, bool> _sendDrop;
    private readonly Func<uint, uint, bool> _sendStackableSplitTo3D;
    private readonly Func<uint, uint, uint, bool> _sendGive;
    private readonly Func<uint, bool> _isComponentPack;
    private readonly AutoWieldController? _autoWield;

    internal HeadlessItemAutomation(
        GameRuntime runtime,
        IRuntimeInteractionTransport transport,
        Func<uint, uint, int, bool> sendPutItemInContainer,
        Func<uint, uint, uint, uint, bool> sendStackableSplitToContainer,
        Func<uint, uint, uint, bool> sendStackableMerge,
        Func<uint, uint, bool> sendUseWithTarget,
        Func<uint, bool> sendDrop,
        Func<uint, uint, bool> sendStackableSplitTo3D,
        Func<uint, uint, uint, bool> sendGive,
        Func<uint, bool>? isComponentPack = null,
        AutoWieldController? autoWield = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sendPutItemInContainer = sendPutItemInContainer
            ?? throw new ArgumentNullException(nameof(sendPutItemInContainer));
        _sendStackableSplitToContainer = sendStackableSplitToContainer
            ?? throw new ArgumentNullException(nameof(sendStackableSplitToContainer));
        _sendStackableMerge = sendStackableMerge
            ?? throw new ArgumentNullException(nameof(sendStackableMerge));
        _sendUseWithTarget = sendUseWithTarget
            ?? throw new ArgumentNullException(nameof(sendUseWithTarget));
        _sendDrop = sendDrop ?? throw new ArgumentNullException(nameof(sendDrop));
        _sendStackableSplitTo3D = sendStackableSplitTo3D
            ?? throw new ArgumentNullException(nameof(sendStackableSplitTo3D));
        _sendGive = sendGive ?? throw new ArgumentNullException(nameof(sendGive));
        _isComponentPack = isComponentPack ?? (_ => false);
        _autoWield = autoWield;
    }

    // Mirrors the GUI automation use, including its auto-wield gate; approach
    // and secure trade are not driven headless.
    internal bool TryUse(uint itemId)
    {
        if (itemId == 0u
            || !AutoWieldIdle
            || _runtime.InventoryOwner.Objects.Get(itemId) is not { } item)
        {
            return false;
        }

        uint useability = item.Useability ?? ItemUseability.Undef;
        if (ItemUseability.IsTargeted(useability))
            return false;

        RuntimeInteractionTransactionState transactions = _runtime.ActionOwner.Transactions;
        long nowMs = checked((long)Math.Floor(
            _runtime.Clock.SimulationTimeSeconds * 1000d));
        if (!transactions.TryConsumeUseThrottle(nowMs))
            return false;
        if (!_runtime.InventoryOwner.Transactions.CanBeginRequest)
            return false;

        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        bool ownedByPlayer = _runtime.InventoryOwner.Objects.IsOwnedByObject(itemId, playerGuid);
        var input = new ItemUsePolicyInput(
            Snapshot(item),
            playerGuid,
            _runtime.InventoryOwner.ExternalContainers.CurrentContainerId,
            ReadyForInventoryRequest: _transport.IsInWorld
                && _runtime.InventoryOwner.Transactions.CanBeginRequest,
            _runtime.InventoryOwner.Vendor.VendorId,
            BypassClassification: true,
            UseCurrentSelection: false,
            SelectedTarget: null,
            ConfirmVolatileRareUses: true,
            InNonCombatMode: _runtime.ActionOwner.Combat.CurrentMode == CombatMode.NonCombat);
        ItemUsePolicyDecision decision = ItemInteractionPolicy.DecideUse(input);
        if (!decision.Actions.Any(
                static action => action.Kind == ItemPolicyActionKind.SendUse))
        {
            return false;
        }

        ItemUseRequestReservation reservation = transactions.BeginUseRequestReservation();
        RuntimeInteractionDispatchResult dispatched;
        try
        {
            dispatched = transactions.TryDispatchUse(
                itemId,
                ownedByPlayer,
                ItemUseability.IsUseable(useability),
                reservation,
                _transport,
                out _);
        }
        catch
        {
            reservation.CancelBeforeDispatch();
            throw;
        }
        return dispatched == RuntimeInteractionDispatchResult.Dispatched;
    }

    // Mirrors the GUI's TryApplyItem, guard checked first since dispatch always marks the wait.
    internal bool TryApply(uint itemId, uint targetId)
    {
        if (itemId == 0u
            || targetId == 0u
            || _runtime.InventoryOwner.Objects.Get(itemId) is not { } item
            || _runtime.InventoryOwner.Objects.Get(targetId) is not { } target)
        {
            return false;
        }

        RuntimeInteractionTransactionState transactions = _runtime.ActionOwner.Transactions;
        long nowMs = checked((long)Math.Floor(
            _runtime.Clock.SimulationTimeSeconds * 1000d));
        if (!transactions.TryConsumeUseThrottle(nowMs))
            return false;
        if (!_runtime.InventoryOwner.Transactions.CanBeginRequest)
            return false;

        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        var input = new ItemUsePolicyInput(
            Snapshot(item),
            playerGuid,
            _runtime.InventoryOwner.ExternalContainers.CurrentContainerId,
            ReadyForInventoryRequest: _transport.IsInWorld
                && _runtime.InventoryOwner.Transactions.CanBeginRequest,
            _runtime.InventoryOwner.Vendor.VendorId,
            BypassClassification: true,
            UseCurrentSelection: true,
            SelectedTarget: Snapshot(target),
            ConfirmVolatileRareUses: true,
            InNonCombatMode: _runtime.ActionOwner.Combat.CurrentMode == CombatMode.NonCombat);
        ItemUsePolicyDecision decision = ItemInteractionPolicy.DecideUse(input);
        if (!decision.Actions.Any(
                static action => action.Kind == ItemPolicyActionKind.SendUseWithTarget))
        {
            return false;
        }

        if (!_transport.IsInWorld)
            return false;

        return transactions.TryDispatchTargetedUse(
            itemId,
            targetId,
            (source, dest) => _sendUseWithTarget(source, dest),
            incrementBusy: true);
    }

    internal bool TryMove(uint itemId, uint containerId, uint amount, int placement)
    {
        if (!AutoWieldIdle || _runtime.InventoryOwner.Objects.Get(itemId) is not { } item)
            return false;

        InventoryTransactionState inventory = _runtime.InventoryOwner.Transactions;
        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint requested = amount == 0u ? fullStack : amount;
        if (requested == 0u || requested > fullStack)
            return false;

        if (requested < fullStack)
        {
            uint clampedPlacement = (uint)Math.Max(0, placement);
            return inventory.TryDispatch(
                InventoryRequestKind.SplitToContainer,
                itemId,
                () => _sendStackableSplitToContainer(
                    itemId, containerId, clampedPlacement, requested));
        }

        return inventory.TryDispatch(
            InventoryRequestKind.PutInContainer,
            itemId,
            () => _sendPutItemInContainer(itemId, containerId, placement));
    }

    // Planner readiness matches the GUI's: no pending request and no auto-wield switch in progress.
    internal bool TryMerge(uint sourceId, uint targetId, uint amount)
    {
        if (_runtime.InventoryOwner.Objects.Get(sourceId) is not { } source
            || _runtime.InventoryOwner.Objects.Get(targetId) is not { } target)
        {
            return false;
        }

        InventoryTransactionState inventory = _runtime.InventoryOwner.Transactions;
        int requested = amount > int.MaxValue ? int.MaxValue : (int)amount;
        StackMergePlan? plan = StackMergePlanner.Plan(
            ToStackMergeItem(source),
            ToStackMergeItem(target),
            inventory.CanBeginRequest && AutoWieldIdle,
            requested);
        if (plan is not { } merge)
            return false;

        return inventory.TryDispatch(
            InventoryRequestKind.Merge,
            sourceId,
            () => _sendStackableMerge(
                merge.SourceObjectId, merge.TargetObjectId, merge.Amount));
    }

    // Mirrors the GUI's TryDropItemForAutomation: full stack drops, a partial stack splits to world.
    internal bool TryDrop(uint itemId, uint amount)
    {
        if (_runtime.InventoryOwner.Objects.Get(itemId) is not { } item)
            return false;

        InventoryTransactionState inventory = _runtime.InventoryOwner.Transactions;
        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint requested = amount == 0u ? fullStack : amount;
        if (requested == 0u || requested > fullStack)
            return false;

        if (requested < fullStack)
        {
            return inventory.TryDispatch(
                InventoryRequestKind.SplitToWorld,
                itemId,
                () => _sendStackableSplitTo3D(itemId, requested));
        }

        return inventory.TryDispatch(
            InventoryRequestKind.DropToWorld,
            itemId,
            () => _sendDrop(itemId));
    }

    // Mirrors the GUI's TryGiveItemForAutomation, same full/partial stack amount as TryDrop.
    internal bool TryGive(uint itemId, uint targetId, uint amount)
    {
        if (_runtime.InventoryOwner.Objects.Get(itemId) is not { } item)
            return false;

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint requested = amount == 0u ? fullStack : amount;
        if (requested == 0u || requested > fullStack)
            return false;

        return _runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Give,
            itemId,
            () => _sendGive(targetId, itemId, requested));
    }

    internal bool TryEquip(uint itemId, uint mask)
    {
        if (itemId == 0u
            || _autoWield is null
            || _autoWield.IsBusy
            || !_transport.IsInWorld
            || !_runtime.InventoryOwner.Transactions.CanBeginRequest)
        {
            return false;
        }
        return _runtime.InventoryOwner.Objects.Get(itemId) is { } item
            && _autoWield.TryWield(item, (EquipMask)mask);
    }

    private bool AutoWieldIdle => _autoWield?.IsBusy != true;

    internal bool EquipmentBusy =>
        _autoWield?.IsBusy == true
        || !_runtime.InventoryOwner.Transactions.CanBeginRequest;

    // The surface requires these bound; not supported on headless yet.
    internal static bool RefusePickup(uint itemId, bool mainPack) => false;
    internal static bool RefuseIdentify(uint itemId) => false;

    private static StackMergeItem ToStackMergeItem(ClientObject item) => new(
        item.ObjectId,
        item.WeenieClassId,
        item.StackSize,
        item.StackSizeMax,
        item.TradeState);

    private static bool IsContainer(ClientObject item) =>
        item.ContainerTypeHint != 0
        || item.Type.HasFlag(ItemType.Container)
        || item.ItemsCapacity > 0;

    private ItemPolicyObject Snapshot(ClientObject item)
    {
        uint playerId = _runtime.PlayerIdentity.ServerGuid;
        var flags = (PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u);
        if (item.ObjectId == playerId)
            flags |= PublicWeenieFlags.Player;
        bool owned = item.ObjectId == playerId
            || IsCarriedByPlayer(item)
            || IsEquippedByPlayer(item);
        int stackSize = Math.Max(1, item.StackSize);
        return new ItemPolicyObject(
            item.ObjectId,
            item.Type,
            flags,
            item.ContainerId,
            item.WielderId,
            item.ValidLocations,
            item.CurrentlyEquippedLocation,
            item.CombatUse ?? 0,
            item.ItemsCapacity,
            item.ContainersCapacity,
            item.Useability ?? 0u,
            item.TargetType ?? 0u,
            owned,
            IsContainer(item),
            item.IsComponentPack || _isComponentPack(item.WeenieClassId),
            item.TradeState,
            stackSize,
            stackSize,
            IsIn3DView: item.ContainerId == 0
                && item.WielderId == 0
                && item.ObjectId != playerId,
            Name: item.GetAppropriateName());
    }

    private bool IsEquippedByPlayer(ClientObject item)
    {
        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        return item.CurrentlyEquippedLocation != EquipMask.None
            && (item.WielderId == playerGuid || item.ContainerId == playerGuid);
    }

    private bool IsCarriedByPlayer(ClientObject item)
    {
        uint playerGuid = _runtime.PlayerIdentity.ServerGuid;
        uint container = item.ContainerId;
        for (int hops = 0; container != 0 && hops < 8; hops++)
        {
            if (container == playerGuid)
                return true;
            container = _runtime.InventoryOwner.Objects.Get(container)?.ContainerId ?? 0u;
        }
        return false;
    }
}
