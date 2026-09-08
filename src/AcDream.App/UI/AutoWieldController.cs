using System;
using AcDream.Core.Combat;
using AcDream.Core.Items;

namespace AcDream.App.UI;

internal sealed class AutoWieldController : IDisposable
{
    internal const EquipMask WeaponReadyMask =
        EquipMask.MeleeWeapon
        | EquipMask.MissileWeapon
        | EquipMask.Held
        | EquipMask.TwoHanded;

    private const byte CombatUseMissile = 0x02;
    private const byte CombatUseTwoHanded = 0x05;

    private static readonly EquipMask[] AutoEquipOrder =
    {
        EquipMask.HeadWear,
        EquipMask.ChestWear,
        EquipMask.AbdomenWear,
        EquipMask.UpperArmWear,
        EquipMask.LowerArmWear,
        EquipMask.HandWear,
        EquipMask.UpperLegWear,
        EquipMask.LowerLegWear,
        EquipMask.FootWear,
        EquipMask.ChestArmor,
        EquipMask.AbdomenArmor,
        EquipMask.UpperArmArmor,
        EquipMask.LowerArmArmor,
        EquipMask.UpperLegArmor,
        EquipMask.LowerLegArmor,
        EquipMask.NeckWear,
        EquipMask.WristWearLeft,
        EquipMask.WristWearRight,
        EquipMask.FingerWearLeft,
        EquipMask.FingerWearRight,
        EquipMask.Shield,
        EquipMask.MissileAmmo,
        EquipMask.MeleeWeapon,
        EquipMask.MissileWeapon,
        EquipMask.Held,
        EquipMask.TwoHanded,
        EquipMask.TrinketOne,
        EquipMask.Cloak,
        EquipMask.SigilOne,
        EquipMask.SigilTwo,
        EquipMask.SigilThree,
    };

    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Action<uint, uint>? _sendWield;
    private readonly Action<uint, uint, int>? _sendPutItemInContainer;
    private readonly Action<string>? _systemMessage;
    private readonly CombatState? _combatState;
    private readonly Action<CombatMode>? _sendChangeCombatMode;
    private readonly InventoryTransactionState? _transactions;

    private PendingSwitch? _pendingSwitch;
    private PendingCombatSettlement? _pendingCombatSettlement;
    private bool _combatTransitionObservedDuringSwitch;
    private bool _disposed;

    public bool IsBusy => _pendingSwitch is not null;

    public AutoWieldController(
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Action<uint, uint>? sendWield,
        Action<uint, uint, int>? sendPutItemInContainer,
        Action<string>? systemMessage = null,
        CombatState? combatState = null,
        Action<CombatMode>? sendChangeCombatMode = null,
        InventoryTransactionState? transactions = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _sendWield = sendWield;
        _sendPutItemInContainer = sendPutItemInContainer;
        _systemMessage = systemMessage;
        _combatState = combatState;
        _sendChangeCombatMode = sendChangeCombatMode;
        _transactions = transactions;

        _objects.ObjectMoved += OnObjectMoved;
        _objects.ObjectRemoved += OnObjectRemoved;
        _objects.MoveRequestFailed += OnMoveRequestFailed;
        _objects.WieldConfirmed += OnWieldConfirmed;
        _objects.Cleared += OnObjectsCleared;
        if (_combatState is not null)
            _combatState.CombatModeChanged += OnCombatModeChanged;
    }

    public bool TryWield(ClientObject item)
    {
        _pendingCombatSettlement = null;
        _combatTransitionObservedDuringSwitch = false;
        return TryWield(item, EquipMask.None, combatModeAfterWield: null);
    }

    public bool TryWield(ClientObject item, EquipMask requestedMask)
    {
        _pendingCombatSettlement = null;
        _combatTransitionObservedDuringSwitch = false;
        return TryWield(item, requestedMask, combatModeAfterWield: null);
    }

    private bool TryWield(
        ClientObject item,
        EquipMask requestedMask,
        CombatMode? combatModeAfterWield)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_pendingSwitch is not null)
            return true;
        if (item.ValidLocations == EquipMask.None)
            return false;
        if (requestedMask != EquipMask.None
            && (requestedMask & item.ValidLocations) != requestedMask)
            return false;

        if (item.CurrentlyEquippedLocation != EquipMask.None)
        {
            if (requestedMask == EquipMask.None
                || requestedMask == item.CurrentlyEquippedLocation)
                return false;
            if (GetEquippedObjectAtLocation(
                requestedMask, priority: 0, item.ObjectId) is { } blocker)
            {
                return BeginWeaponReplacement(
                    item.ObjectId,
                    blocker,
                    requestedMask,
                    combatModeAfterWield);
            }
            return SendWield(item, requestedMask, combatModeAfterWield);
        }

        if (ItemEquipRules.IsAutoWearItem(item))
        {
            if (!AutoWearIsLegal(item, out ClientObject? blocker))
            {
                if (blocker is not null)
                {
                    _systemMessage?.Invoke(
                        $"You must remove your {blocker.GetAppropriateName()} to wear that");
                }
                return false;
            }

            return SendWield(
                item,
                item.ValidLocations,
                combatModeAfterWield: null);
        }

        EquipMask weaponLocation = requestedMask == EquipMask.None
            ? item.ValidLocations & WeaponReadyMask
            : requestedMask & WeaponReadyMask;
        if (weaponLocation != EquipMask.None)
        {
            combatModeAfterWield ??= (_combatState?.CurrentMode ?? CombatMode.NonCombat)
                == CombatMode.NonCombat
                ? null
                : CombatModeForWeaponLocation(weaponLocation);

            ClientObject? blocker = GetEquippedObjectAtLocation(
                WeaponReadyMask, priority: 0, item.ObjectId);
            if (blocker is not null)
                return BeginWeaponReplacement(
                    item.ObjectId,
                    blocker,
                    weaponLocation,
                    combatModeAfterWield);

            if (BlocksUseOfShield(item)
                && GetEquippedObjectAtLocation(
                    EquipMask.Shield, priority: 0, item.ObjectId) is { } shield)
                return BeginWeaponReplacement(
                    item.ObjectId,
                    shield,
                    weaponLocation,
                    combatModeAfterWield);

            if (item.AmmoType is > 0
                && GetEquippedObjectAtLocation(
                    EquipMask.MissileAmmo, priority: 0, item.ObjectId) is { } ammo
                && ammo.AmmoType != item.AmmoType)
                return BeginWeaponReplacement(
                    item.ObjectId,
                    ammo,
                    weaponLocation,
                    combatModeAfterWield);

            return SendWield(item, weaponLocation, combatModeAfterWield);
        }

        EquipMask mask = requestedMask != EquipMask.None
            ? requestedMask
            : BestAvailableEquipMask(item);
        if (mask == EquipMask.None)
        {
            mask = FirstCompatibleEquipMask(item);
            ClientObject? blocker = GetEquippedObjectAtLocation(
                mask, priority: 0, item.ObjectId);
            return blocker is not null
                && BeginWeaponReplacement(
                    item.ObjectId,
                    blocker,
                    mask,
                    combatModeAfterWield: null);
        }

        return SendWield(item, mask, combatModeAfterWield: null);
    }

    private bool BeginWeaponReplacement(
        uint requestedItemId,
        ClientObject blockingItem,
        EquipMask requestedMask,
        CombatMode? combatModeAfterWield)
    {
        if (_sendPutItemInContainer is null)
            return false;

        uint player = _playerGuid();
        if (player == 0)
            return false;

        _pendingSwitch = new PendingSwitch(
            requestedItemId,
            blockingItem.ObjectId,
            requestedMask,
            combatModeAfterWield);

        _systemMessage?.Invoke(
            $"Moving {blockingItem.GetAppropriateName()} to your backpack");
        bool dispatched = DispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            blockingItem.ObjectId,
            () =>
            {
                _sendPutItemInContainer(blockingItem.ObjectId, player, 0);
                return true;
            });
        if (!dispatched)
            _pendingSwitch = null;
        return dispatched;
    }

    private bool SendWield(
        ClientObject item,
        EquipMask mask,
        CombatMode? combatModeAfterWield)
    {
        if (_sendWield is null)
            return false;
        _pendingSwitch = new PendingSwitch(
            item.ObjectId,
            BlockingItemId: 0,
            RequestedMask: mask,
            CombatModeAfterWield: combatModeAfterWield);
        bool dispatched = DispatchInventoryRequest(
            InventoryRequestKind.Wield,
            item.ObjectId,
            () =>
            {
                _sendWield(item.ObjectId, (uint)mask);
                return true;
            });
        if (!dispatched)
            _pendingSwitch = null;
        return dispatched;
    }

    private bool DispatchInventoryRequest(
        InventoryRequestKind kind,
        uint itemId,
        Func<bool> dispatch)
        => _transactions?.TryDispatch(kind, itemId, dispatch) ?? dispatch();

    private void OnObjectMoved(ClientObjectMove move)
    {
        if (_pendingSwitch is not { } pending
            || pending.BlockingItemId == 0
            || move.ItemId != pending.BlockingItemId
            || move.Current.ContainerId != _playerGuid()
            || move.Current.EquipLocation != EquipMask.None)
            return;

        _pendingSwitch = null;
        if (_objects.Get(pending.RequestedItemId) is { } requested)
            TryWield(
                requested,
                pending.RequestedMask,
                pending.CombatModeAfterWield);
    }

    private void OnWieldConfirmed(uint itemId)
    {
        if (_pendingSwitch is { BlockingItemId: 0 } pending
            && itemId == pending.RequestedItemId)
        {
            _pendingSwitch = null;
            if (pending.CombatModeAfterWield is { } readyMode)
            {
                _pendingCombatSettlement = new(
                    itemId,
                    readyMode,
                    CombatSettlementPhase.AwaitingPostWieldMode,
                    _combatTransitionObservedDuringSwitch);
            }
            _combatTransitionObservedDuringSwitch = false;
        }
    }

    private void OnCombatModeChanged(CombatMode mode)
    {
        if (_pendingSwitch is { CombatModeAfterWield: { } switchReadyMode }
            && mode != switchReadyMode)
        {
            _combatTransitionObservedDuringSwitch = true;
        }

        if (_pendingCombatSettlement is not { } settlement)
            return;
        if (mode == settlement.ReadyMode)
        {
            if (settlement.Phase == CombatSettlementPhase.SawTransitionalPeace
                || settlement.ExpectTrailingPeace)
            {
                _pendingCombatSettlement = settlement with
                {
                    Phase = CombatSettlementPhase.SawReadyAfterPeace,
                };
            }
            else
            {
                _pendingCombatSettlement = null;
            }
            return;
        }

        if (mode != CombatMode.NonCombat)
            return;

        if (settlement.Phase == CombatSettlementPhase.SawReadyAfterPeace)
        {
            _pendingCombatSettlement = null;
            _sendChangeCombatMode?.Invoke(settlement.ReadyMode);
        }
        else
        {
            _pendingCombatSettlement = settlement with
            {
                Phase = CombatSettlementPhase.SawTransitionalPeace,
            };
        }
    }

    private void OnMoveRequestFailed(MoveRequestFailure failure)
    {
        if (_pendingSwitch is { } pending
            && (failure.ItemId == pending.BlockingItemId
                || failure.ItemId == pending.RequestedItemId))
        {
            _pendingSwitch = null;
            _combatTransitionObservedDuringSwitch = false;
        }
    }

    private void OnObjectRemoved(ClientObject item)
    {
        if (_pendingSwitch is { } pending
            && (item.ObjectId == pending.BlockingItemId
                || item.ObjectId == pending.RequestedItemId))
        {
            _pendingSwitch = null;
            _combatTransitionObservedDuringSwitch = false;
        }
        if (_pendingCombatSettlement is { } settlement
            && item.ObjectId == settlement.ItemId)
            _pendingCombatSettlement = null;
    }

    private void OnObjectsCleared()
    {
        _pendingSwitch = null;
        _pendingCombatSettlement = null;
        _combatTransitionObservedDuringSwitch = false;
    }

    public void NotifyExplicitCombatModeRequest()
    {
        _pendingCombatSettlement = null;
        _combatTransitionObservedDuringSwitch = false;
        if (_pendingSwitch is { } pending)
            _pendingSwitch = pending with { CombatModeAfterWield = null };
    }

    private EquipMask BestAvailableEquipMask(ClientObject item)
    {
        foreach (EquipMask mask in AutoEquipOrder)
        {
            if ((item.ValidLocations & mask) == EquipMask.None)
                continue;
            if (!EquipMaskOccupied(mask, item.ObjectId))
                return mask;
        }
        return EquipMask.None;
    }

    private static EquipMask FirstCompatibleEquipMask(ClientObject item)
    {
        foreach (EquipMask mask in AutoEquipOrder)
            if ((item.ValidLocations & mask) != EquipMask.None)
                return mask;
        return EquipMask.None;
    }

    private bool AutoWearIsLegal(
        ClientObject item,
        out ClientObject? blocker)
    {
        uint priorityMask = EquippedAutoWearPriorityMask(item.ObjectId);
        if ((item.Priority & priorityMask) == 0)
        {
            blocker = null;
            return true;
        }

        EquipMask occupiedLocations = EquippedAutoWearLocationMask(item.ObjectId)
            & item.ValidLocations;
        blocker = GetEquippedObjectAtLocation(
            occupiedLocations, item.Priority, item.ObjectId);
        return false;
    }

    private bool EquipMaskOccupied(EquipMask mask, uint exceptGuid)
        => GetEquippedObjectAtLocation(mask, priority: 0, exceptGuid) is not null;

    private uint EquippedAutoWearPriorityMask(uint exceptGuid)
    {
        uint mask = 0;
        foreach (ClientObject item in _objects.GetEquippedBy(_playerGuid()))
        {
            if (item.ObjectId == exceptGuid
                || (item.CurrentlyEquippedLocation & ItemEquipRules.AutoWearMask) == EquipMask.None)
                continue;
            mask |= item.Priority;
        }
        return mask;
    }

    private EquipMask EquippedAutoWearLocationMask(uint exceptGuid)
    {
        EquipMask mask = EquipMask.None;
        foreach (ClientObject item in _objects.GetEquippedBy(_playerGuid()))
        {
            if (item.ObjectId == exceptGuid
                || (item.CurrentlyEquippedLocation & ItemEquipRules.AutoWearMask) == EquipMask.None)
                continue;
            mask |= item.CurrentlyEquippedLocation;
        }
        return mask;
    }

    private ClientObject? GetEquippedObjectAtLocation(
        EquipMask locationMask,
        uint priority,
        uint exceptGuid)
    {
        if (locationMask == EquipMask.None)
            return null;

        foreach (ClientObject item in _objects.GetEquippedBy(_playerGuid()))
        {
            if (item.ObjectId == exceptGuid
                || (item.CurrentlyEquippedLocation & locationMask) == EquipMask.None)
                continue;
            if ((item.Priority & priority) != 0 || priority == 0)
                return item;
        }
        return null;
    }

    internal static bool BlocksUseOfShield(ClientObject item)
    {
        byte combatUse = item.CombatUse ?? 0;
        return combatUse == CombatUseTwoHanded
            || (combatUse == CombatUseMissile && item.AmmoType is > 0)
            || item.Type.HasFlag(ItemType.Caster);
    }

    private static CombatMode? CombatModeForWeaponLocation(EquipMask location)
    {
        if ((location & EquipMask.MissileWeapon) != 0)
            return CombatMode.Missile;
        if ((location & EquipMask.Held) != 0)
            return CombatMode.Magic;
        if ((location & (EquipMask.MeleeWeapon | EquipMask.TwoHanded)) != 0)
            return CombatMode.Melee;
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pendingSwitch = null;
        _pendingCombatSettlement = null;
        _combatTransitionObservedDuringSwitch = false;
        _objects.ObjectMoved -= OnObjectMoved;
        _objects.ObjectRemoved -= OnObjectRemoved;
        _objects.MoveRequestFailed -= OnMoveRequestFailed;
        _objects.WieldConfirmed -= OnWieldConfirmed;
        _objects.Cleared -= OnObjectsCleared;
        if (_combatState is not null)
            _combatState.CombatModeChanged -= OnCombatModeChanged;
    }

    private readonly record struct PendingSwitch(
        uint RequestedItemId,
        uint BlockingItemId,
        EquipMask RequestedMask,
        CombatMode? CombatModeAfterWield);

    private readonly record struct PendingCombatSettlement(
        uint ItemId,
        CombatMode ReadyMode,
        CombatSettlementPhase Phase,
        bool ExpectTrailingPeace);

    private enum CombatSettlementPhase
    {
        AwaitingPostWieldMode,
        SawTransitionalPeace,
        SawReadyAfterPeace,
    }
}
