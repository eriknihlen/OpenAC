using System;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;


namespace AcDream.Runtime.Gameplay;

public enum ItemPrimaryClickResult
{
    NotActive,
    ConsumedSuccess,
    ConsumedRejected,
}

public readonly record struct PendingBackpackPlacement(
    ulong Token,
    uint ItemId,
    uint ContainerId,
    int Placement,
    ClientObject? ItemIdentity);

public sealed class RuntimeItemInteraction : IDisposable
{
    internal const string InventoryRequestBusyMessage =
        "You can only move or use one item at a time";
    private const long RetailDoubleClickMs = 500;

    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Func<long> _nowMs;
    private readonly Action<uint>? _sendUse;
    private readonly Action<uint>? _sendExamine;
    private readonly Action<uint, uint>? _sendUseWithTarget;
    private readonly Action<uint, uint>? _sendWield;
    private readonly Action<uint>? _sendDrop;
    private readonly Action<uint, uint>? _sendSplitToWorld;
    private readonly Action<uint, uint, int>? _sendPutItemInContainer;
    private readonly Action<uint, uint, uint, uint>? _sendSplitToContainer;
    private readonly Action<uint, uint, uint>? _sendStackableMerge;
    private readonly Action<uint, uint, uint>? _sendGive;
    private Action<string>? _toast;
    private readonly Func<bool> _readyForInventoryRequest;
    private readonly Func<uint> _activeVendorId;
    private readonly Func<uint> _groundObjectId;
    private Func<bool> _playerOnGround;
    private readonly Func<bool> _inNonCombatMode;
    private Func<uint, bool> _isComponentPack;
    private Action<uint, uint, int>? _placeInBackpack;
    private Func<uint> _backpackContainerId;
    private readonly Action<uint>? _requestExternalContainer;
    private readonly Action<ItemPolicyAction>? _auxiliaryAction;
    private readonly InteractionState _interactionState;
    private readonly Func<uint> _selectedObjectId;
    private StackSplitQuantityState? _stackSplitQuantity;
    private readonly Func<bool> _dragOnPlayerOpensSecureTrade;
    private readonly Func<bool> _mainPackPreferred;
    private readonly Func<bool> _confirmVolatileRareUses;
    private readonly Action<string>? _systemMessage;
    private readonly Action<string, RetailLogTextType>? _interfaceText;
    private readonly AutoWieldController _autoWield;
    private Action<uint, ItemUseRequestReservation>? _requestUse;
    private readonly Func<uint, uint, int, uint, bool>? _sendBuy;
    private readonly Func<uint, IReadOnlyList<(int Amount, uint ItemGuid)>, uint, bool>? _sendBuyAll;
    private readonly Func<uint, IReadOnlyList<(int Amount, uint ItemGuid)>, bool>? _sendSell;
    private readonly Func<uint, IReadOnlyList<uint>, bool>? _sendSalvage;
    private readonly RuntimeInteractionTransactionState _runtimeTransactions;
    private readonly InventoryTransactionState _transactions;

    private uint _consumedPrimaryClickTarget;
    private long _consumedPrimaryClickMs = long.MinValue / 2;
    private PendingBackpackPlacement? _pendingBackpackPlacement;
    private bool _disposed;

    public RuntimeItemInteraction(
        ClientObjectTable objects,
        RuntimeInteractionTransactionState runtimeTransactions,
        InteractionState interactionState,
        Func<uint> playerGuid,
        Action<uint>? sendUse,
        Action<uint, uint>? sendUseWithTarget,
        Action<uint, uint>? sendWield,
        Action<uint>? sendDrop,
        Action<uint>? sendExamine = null,
        Func<long>? nowMs = null,
        Action<string>? toast = null,
        Func<bool>? readyForInventoryRequest = null,
        Func<uint>? activeVendorId = null,
        Func<uint>? groundObjectId = null,
        Func<bool>? playerOnGround = null,
        Func<bool>? inNonCombatMode = null,
        Func<uint, bool>? isComponentPack = null,
        Action<uint, uint, int>? placeInBackpack = null,
        Func<uint>? backpackContainerId = null,
        Action<ItemPolicyAction>? auxiliaryAction = null,
        Action<uint, uint>? sendSplitToWorld = null,
        Func<uint>? selectedObjectId = null,
        StackSplitQuantityState? stackSplitQuantity = null,
        Action<uint, uint, int>? sendPutItemInContainer = null,
        Action<uint, uint, uint>? sendGive = null,
        Func<bool>? dragOnPlayerOpensSecureTrade = null,
        Func<bool>? mainPackPreferred = null,
        Func<bool>? confirmVolatileRareUses = null,
        Action<string>? systemMessage = null,
        Action<uint, uint, uint, uint>? sendSplitToContainer = null,
        Action<uint>? requestExternalContainer = null,
        CombatState? combatState = null,
        Action<CombatMode>? sendChangeCombatMode = null,
        Action<uint, ItemUseRequestReservation>? requestUse = null,
        Func<uint, uint, int, uint, bool>? sendBuy = null,
        Func<uint, IReadOnlyList<(int Amount, uint ItemGuid)>, uint, bool>? sendBuyAll = null,
        Func<uint, IReadOnlyList<(int Amount, uint ItemGuid)>, bool>? sendSell = null,
        Action<string, RetailLogTextType>? interfaceText = null,
        Action<uint, uint, uint>? sendStackableMerge = null,
        Func<uint, IReadOnlyList<uint>, bool>? sendSalvage = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _sendUse = sendUse;
        _sendExamine = sendExamine;
        _sendUseWithTarget = sendUseWithTarget;
        _sendWield = sendWield;
        _sendDrop = sendDrop;
        _sendSplitToWorld = sendSplitToWorld;
        _sendPutItemInContainer = sendPutItemInContainer;
        _sendSplitToContainer = sendSplitToContainer;
        _sendStackableMerge = sendStackableMerge;
        _sendGive = sendGive;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _toast = toast;
        _readyForInventoryRequest = readyForInventoryRequest ?? (() => true);
        _activeVendorId = activeVendorId ?? (() => 0u);
        _groundObjectId = groundObjectId ?? (() => 0u);
        _playerOnGround = playerOnGround ?? (() => true);
        _inNonCombatMode = inNonCombatMode ?? (() => false);
        _isComponentPack = isComponentPack ?? (_ => false);
        _placeInBackpack = placeInBackpack;
        _backpackContainerId = backpackContainerId ?? _playerGuid;
        _requestExternalContainer = requestExternalContainer;
        _auxiliaryAction = auxiliaryAction;
        _selectedObjectId = selectedObjectId ?? (() => 0u);
        _stackSplitQuantity = stackSplitQuantity;
        _dragOnPlayerOpensSecureTrade = dragOnPlayerOpensSecureTrade ?? (() => true);
        _mainPackPreferred = mainPackPreferred ?? (() => false);
        _confirmVolatileRareUses = confirmVolatileRareUses ?? (() => true);
        _systemMessage = systemMessage;
        _interfaceText = interfaceText;
        _requestUse = requestUse;
        _sendBuy = sendBuy;
        _sendBuyAll = sendBuyAll;
        _sendSell = sendSell;
        _sendSalvage = sendSalvage;
        _interactionState = interactionState
            ?? throw new ArgumentNullException(nameof(interactionState));
        _runtimeTransactions = runtimeTransactions
            ?? throw new ArgumentNullException(nameof(runtimeTransactions));
        _transactions = _runtimeTransactions.Inventory;
        if (!ReferenceEquals(_transactions.Objects, _objects))
        {
            throw new ArgumentException(
                "The inventory transaction owner must borrow the controller's exact object table.",
                nameof(runtimeTransactions));
        }
        _autoWield = new AutoWieldController(
            _objects,
            _playerGuid,
            _sendWield is { } wield ? (i, m) => { wield(i, m); return true; } : null,
            sendPutItemInContainer is { } putInContainer
                ? (i, c, s) => { putInContainer(i, c, s); return true; }
                : null,
            _systemMessage,
            combatState,
            sendChangeCombatMode,
            _transactions);
        _interactionState.Changed += OnInteractionModeChanged;
        _transactions.StateChanged += OnTransactionStateChanged;
        _transactions.RequestCompleted += OnInventoryRequestCompleted;
        _transactions.RequestFailed += OnInventoryRequestFailed;
        _transactions.ObjectTableCleared += OnInventoryObjectsCleared;
        _objects.MoveRequestFailed += OnMoveRequestFailedNotice;
    }

    /// <summary>
    /// Installs the walk-to-then-act route. A host with a window knows how
    /// far away an object is and can close the distance first; without one
    /// the request goes out immediately and the server judges the range.
    /// </summary>
    public void BindApproachRoute(
        Action<uint, ItemUseRequestReservation>? requestUse,
        Action<uint, uint, int>? placeInBackpack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (requestUse is not null)
            _requestUse = requestUse;
        if (placeInBackpack is not null)
            _placeInBackpack = placeInBackpack;
    }

    /// <summary>
    /// Installs the answers only a presented client has: which pack the
    /// player currently has open, how much of a stack the split control is
    /// asking for, whether the player is standing on the ground, and where
    /// a transient notice goes.
    /// </summary>
    public void BindPresentation(
        Func<uint>? backpackContainerId = null,
        StackSplitQuantityState? stackSplitQuantity = null,
        Func<bool>? playerOnGround = null,
        Action<string>? toast = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (backpackContainerId is not null)
            _backpackContainerId = backpackContainerId;
        if (stackSplitQuantity is not null)
            _stackSplitQuantity = stackSplitQuantity;
        if (playerOnGround is not null)
            _playerOnGround = playerOnGround;
        if (toast is not null)
            _toast = toast;
    }

    /// <summary>
    /// Component packs are named by the magic catalogue, which is read from
    /// the installed data files rather than from the wire.
    /// </summary>
    public void BindComponentPackResolver(Func<uint, bool> isComponentPack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(isComponentPack);
        _isComponentPack = isComponentPack;
    }

    public event Action? StateChanged;

    public event Action<uint, uint>? MergeAttempted;

    public event Action<uint, uint>? SecureTradeRequested;

    public event Action<PendingBackpackPlacement>? PendingBackpackPlacementRequested;

    public event Action<PendingBackpackPlacement>? PendingBackpackPlacementCancelled;

    /// <summary>
    /// Resolves the exact waiting projection after an authoritative response.
    /// The token prevents a late response for a recycled GUID from erasing a
    /// newer incarnation's projection in the inventory panel.
    /// </summary>
    public event Action<PendingBackpackPlacement>? PendingBackpackPlacementResolved;

    public event Action<WorldDropDispatch>? WorldDropDispatched;

    public uint PlayerGuid => _playerGuid();

    public InteractionState InteractionState => _interactionState;
    public RuntimeInteractionTransactionState RuntimeTransactions =>
        _runtimeTransactions;

    /// <summary>
    /// True once the owner has detached from the state it borrows. A host
    /// composition that fails and rolls back must leave this false: the
    /// owner outlives any one host composition.
    /// </summary>
    public bool IsDisposed => _disposed;

    public int BusyCount => _transactions.BusyCount;

    /// <summary>
    /// Outstanding appraisal requests, counted apart from
    /// <see cref="BusyCount"/>. A description in flight holds nothing an
    /// item action needs, so it never gates one; the cursor still shows the
    /// wait, because the character is waiting on the server either way. A
    /// wait already past its own bound counts for nothing: the answer is not
    /// coming, and the cursor must not say the character is still waiting.
    /// </summary>
    public int AppraisalCount =>
        _runtimeTransactions.IsAwaitingAppraisalExpired
            ? 0
            : _transactions.AppraisalCount;

    /// <summary>
    /// Whether another description may be asked for. One at a time, except
    /// that a wait past its own bound no longer counts as one.
    /// </summary>
    public bool CanBeginAppraisal => _runtimeTransactions.CanBeginAppraisal;
    public uint CurrentAppraisalId =>
        _runtimeTransactions.CurrentAppraisalId;

    public bool CanMakeInventoryRequest =>
        BaseCanMakeInventoryRequest && !_transactions.HasPendingRequest;

    private bool BaseCanMakeInventoryRequest =>
        _readyForInventoryRequest()
        && _transactions.BusyCount == 0
        && !_autoWield.IsBusy;

    public bool EnsureInventoryRequestReady()
    {
        if (CanMakeInventoryRequest)
            return true;

        if (_transactions.HasPendingRequest
            || _transactions.BusyCount != 0
            || _autoWield.IsBusy)
            _systemMessage?.Invoke(InventoryRequestBusyMessage);
        return false;
    }

    public bool TryBuy(uint vendorGuid, uint itemGuid, int amount, uint alternateCurrencyId)
    {
        if (vendorGuid == 0u || itemGuid == 0u || amount <= 0 || _sendBuy is null)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;

        return DispatchShop(vendorGuid,
            () => _sendBuy(vendorGuid, itemGuid, amount, alternateCurrencyId));
    }

    public bool TryBuyAll(
        uint vendorGuid,
        IReadOnlyList<(int Amount, uint ItemGuid)> items,
        uint alternateCurrencyId)
    {
        if (vendorGuid == 0u || items is null || items.Count == 0 || _sendBuyAll is null)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;

        return DispatchShop(vendorGuid,
            () => _sendBuyAll(vendorGuid, items, alternateCurrencyId));
    }

    public bool TrySell(
        uint vendorGuid,
        IReadOnlyList<(int Amount, uint ItemGuid)> items)
    {
        if (vendorGuid == 0u || items is null || items.Count == 0 || _sendSell is null)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;

        return DispatchShop(vendorGuid,
            () => _sendSell(vendorGuid, items));
    }

    private bool DispatchShop(uint vendorGuid, Func<bool> send) =>
        _transactions.TryDispatch(InventoryRequestKind.Shop, vendorGuid, () =>
        {
            ItemUseRequestReservation reservation = BeginUseRequestReservation();
            try
            {
                if (!send())
                {
                    reservation.CancelBeforeDispatch();
                    return false;
                }
                reservation.MarkDispatched();
                return true;
            }
            catch
            {
                reservation.CancelBeforeDispatch();
                throw;
            }
        });

    public void ReportPendingBackpackPlacementConflict()
    {
        if (_pendingBackpackPlacement is not { } pending)
            return;
        string? itemName = _objects.Get(pending.ItemId)?.Name;
        string name = string.IsNullOrWhiteSpace(itemName) ? "that item" : itemName;
        _systemMessage?.Invoke($"Already attempting to place {name} here");
    }

    public bool TryDispatchInventoryRequest(
        InventoryRequestKind kind,
        uint itemId,
        Func<bool> dispatch,
        ulong reservationToken = 0u)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (itemId == 0u)
            return false;

        if (reservationToken != 0u)
        {
            try
            {
                return _transactions.TryDispatch(
                    kind,
                    itemId,
                    dispatch,
                    reservationToken);
            }
            catch
            {
                CancelPendingBackpackPlacement(itemId, reservationToken);
                throw;
            }
        }

        if (!EnsureInventoryRequestReady())
            return false;
        return _transactions.TryDispatch(kind, itemId, dispatch);
    }

    public bool TryGetPendingInventoryRequest(out PendingInventoryRequest pending)
        => _transactions.TryGetPending(out pending);

    public bool TrySplitToContainer(
        uint itemId,
        uint containerId,
        uint placement,
        uint amount)
    {
        if (itemId == 0u
            || containerId == 0u
            || _sendSplitToContainer is null
            || _objects.Get(itemId) is not { } item)
        {
            return false;
        }

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        if (amount == 0u || amount >= fullStack)
            return false;

        return TryDispatchInventoryRequest(
            InventoryRequestKind.SplitToContainer,
            itemId,
            () =>
            {
                _sendSplitToContainer(itemId, containerId, placement, amount);
                return true;
            });
    }

    public bool TryMoveItemForAutomation(
        uint itemId,
        uint containerId,
        uint amount = 0u,
        int placement = 0)
        => TryMoveItemForAutomation(
            itemId, containerId, amount, placement, joinStack: false);

    /// <summary>
    /// The automation move, optionally joining a stack first. With
    /// <paramref name="joinStack"/> the container and the packs inside it
    /// are searched for a stack of the same thing with room for everything
    /// being moved, and the first one found is joined with a merge; that is
    /// settled before any room check, because joining takes no slot. With no
    /// such stack, or without the flag, the move is the plain put or split.
    /// </summary>
    public bool TryMoveItemForAutomation(
        uint itemId,
        uint containerId,
        uint amount,
        int placement,
        bool joinStack)
    {
        if (itemId == 0u
            || containerId == 0u
            || _sendPutItemInContainer is null
            || _objects.Get(itemId) is not { } item
            || !IsOwnedByPlayer(itemId)
            || (containerId != _playerGuid() && !IsOwnedByPlayer(containerId)))
        {
            return false;
        }

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint requested = amount == 0u ? fullStack : amount;
        if (requested == 0u || requested > fullStack)
            return false;
        if (joinStack
            && TryPlanAutoMerge(itemId, containerId, requested) is { } join)
        {
            return TryDispatchMerge(join);
        }
        if (requested < fullStack)
        {
            return TrySplitToContainer(
                itemId,
                containerId,
                (uint)Math.Max(0, placement),
                requested);
        }

        return TryDispatchInventoryRequest(
            InventoryRequestKind.PutInContainer,
            itemId,
            () =>
            {
                _sendPutItemInContainer(itemId, containerId, placement);
                return true;
            });
    }

    public bool TryMergeItemsForAutomation(
        uint sourceItemId,
        uint targetItemId,
        uint amount = 0u)
    {
        if (_sendStackableMerge is null
            || !IsOwnedByPlayer(sourceItemId)
            || !IsOwnedByPlayer(targetItemId)
            || _objects.Get(sourceItemId) is not { } source
            || _objects.Get(targetItemId) is not { } target)
        {
            return false;
        }

        int requested = amount > int.MaxValue ? int.MaxValue : (int)amount;
        StackMergePlan? plan = StackMergePlanner.Plan(
            ToStackMergeItem(source),
            ToStackMergeItem(target),
            CanMakeInventoryRequest,
            requested);
        if (plan is not { } merge)
            return false;

        return TryDispatchMerge(merge);
    }

    private bool TryDispatchMerge(StackMergePlan merge) =>
        TryDispatchInventoryRequest(
            InventoryRequestKind.Merge,
            merge.SourceObjectId,
            () =>
            {
                _sendStackableMerge!(
                    merge.SourceObjectId,
                    merge.TargetObjectId,
                    merge.Amount);
                MergeAttempted?.Invoke(
                    merge.SourceObjectId,
                    merge.TargetObjectId);
                return true;
            });

    public bool TryDropItemForAutomation(uint itemId, uint amount = 0u)
    {
        if (!IsOwnedByPlayer(itemId) || _objects.Get(itemId) is not { } item)
            return false;

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint requested = amount == 0u ? fullStack : amount;
        if (requested == 0u || requested > fullStack)
            return false;
        InventoryRequestKind kind = requested < fullStack
            ? InventoryRequestKind.SplitToWorld
            : InventoryRequestKind.DropToWorld;
        return TryDispatchInventoryRequest(
            kind,
            itemId,
            () =>
            {
                if (requested < fullStack)
                {
                    if (_sendSplitToWorld is null)
                        return false;
                    _sendSplitToWorld(itemId, requested);
                }
                else
                {
                    if (_sendDrop is null)
                        return false;
                    _sendDrop(itemId);
                }
                return true;
            });
    }

    public bool TryGiveItemForAutomation(
        uint itemId,
        uint targetId,
        uint amount = 0u)
    {
        if (_sendGive is null
            || targetId == 0u
            || targetId == _playerGuid()
            || _objects.Get(targetId) is null
            || !IsOwnedByPlayer(itemId)
            || _objects.Get(itemId) is not { } item)
        {
            return false;
        }

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        uint requested = amount == 0u ? fullStack : amount;
        if (requested == 0u || requested > fullStack)
            return false;
        return TryDispatchInventoryRequest(
            InventoryRequestKind.Give,
            itemId,
            () =>
            {
                _sendGive(targetId, itemId, requested);
                return true;
            });
    }

    public bool TrySalvageItemsForAutomation(
        uint toolId,
        IReadOnlyList<uint> itemIds)
        => TrySalvageItems(toolId, itemIds);

    public bool TrySalvageItems(uint toolId, IReadOnlyList<uint> itemIds)
    {
        if (_sendSalvage is null
            || toolId == 0u
            || itemIds is null
            || itemIds.Count == 0
            || !CanMakeInventoryRequest
            || !IsOwnedByPlayer(toolId)
            || _objects.Get(toolId) is not { } tool
            || (tool.Type & ItemType.TinkeringTool) == 0)
        {
            return false;
        }

        var distinct = new HashSet<uint>();
        foreach (uint itemId in itemIds)
        {
            if (itemId == 0u
                || itemId == toolId
                || !distinct.Add(itemId)
                || !IsOwnedByPlayer(itemId)
                || _objects.Get(itemId) is not { } item
                || !SalvageItemPolicy.IsSuitable(item))
            {
                return false;
            }
        }
        return _sendSalvage(toolId, itemIds);
    }

    public void IncrementBusyCount()
        => _runtimeTransactions.IncrementBusyCount();

    public event Action<ItemPolicyAction>? PolicyActionRequested;

    public uint PendingSourceItem
        => _interactionState.Current is { Kind: InteractionModeKind.UseItemOnTarget } mode
            ? mode.SourceObjectId
            : 0u;

    public bool IsTargetModeActive
        => _interactionState.Current.Kind == InteractionModeKind.UseItemOnTarget;

    public bool IsAnyTargetModeActive
        => _interactionState.Current.Kind != InteractionModeKind.None;

    public bool IsPendingSource(uint itemGuid)
        => itemGuid != 0 && itemGuid == PendingSourceItem;

    public bool IsPendingInventorySource(uint itemGuid)
        => itemGuid != 0
            && _transactions.TryGetPending(out PendingInventoryRequest pending)
            && pending.ItemId == itemGuid;

    public void ReportClientLocal(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        if (_interfaceText is not null)
            _interfaceText(message, RetailLogTextType.ClientLocal);
        else if (_systemMessage is not null)
            _systemMessage(message);
        else
            _toast?.Invoke(message);
    }

    public bool IsOwnedByPlayer(uint itemGuid)
        => _objects.IsOwnedByObject(itemGuid, _playerGuid());

    public bool IsCurrentTargetCompatible(uint targetGuid)
    {
        if (!IsTargetModeActive || targetGuid == 0) return false;

        var source = _objects.Get(PendingSourceItem);
        return source?.Useability is not null
            && TargetCompatible(source, targetGuid);
    }

    public ItemPrimaryClickResult OfferPrimaryClick(uint targetGuid)
    {
        InteractionModeKind mode = _interactionState.Current.Kind;
        if (mode == InteractionModeKind.None)
        {
            long now = _nowMs();
            if (targetGuid != 0
                && targetGuid == _consumedPrimaryClickTarget
                && now - _consumedPrimaryClickMs <= RetailDoubleClickMs)
                return ItemPrimaryClickResult.ConsumedRejected;
            _consumedPrimaryClickTarget = 0;
            return ItemPrimaryClickResult.NotActive;
        }

        bool accepted;
        switch (mode)
        {
            case InteractionModeKind.Use:
                ClearTargetMode();
                accepted = ActivateItem(targetGuid);
                break;
            case InteractionModeKind.Examine:
                ClearTargetMode();
                accepted = targetGuid != 0 && _sendExamine is not null;
                if (accepted)
                    RequestAppraisal(targetGuid);
                break;
            case InteractionModeKind.UseItemOnTarget:
                accepted = AcquireTarget(targetGuid);
                break;
            default:
                throw new InvalidOperationException($"Unknown interaction mode {mode}.");
        }
        _consumedPrimaryClickTarget = targetGuid;
        _consumedPrimaryClickMs = _nowMs();
        return accepted
            ? ItemPrimaryClickResult.ConsumedSuccess
            : ItemPrimaryClickResult.ConsumedRejected;
    }

    public ItemPrimaryClickResult OfferSelfPrimaryClick()
        => OfferPrimaryClick(_playerGuid());

    public bool UseSelectedOrEnterMode(uint selectedObjectId)
    {
        if (selectedObjectId != 0)
            return ActivateItem(selectedObjectId);
        return _interactionState.EnterUse();
    }

    public bool ExamineSelectedOrEnterMode(uint selectedObjectId)
    {
        if (selectedObjectId == 0)
            return _interactionState.EnterExamine();
        if (_sendExamine is null)
            return false;
        RequestAppraisal(selectedObjectId);
        return true;
    }

    private void RequestAppraisal(uint objectId)
    {
        if (objectId == 0 || _sendExamine is null)
            return;
        _runtimeTransactions.TryRequestAppraisal(objectId, _sendExamine);
    }

    public bool TryAppraiseForAutomation(uint objectId)
    {
        if (objectId == 0u
            || _sendExamine is null
            || _objects.Get(objectId) is null)
        {
            return false;
        }
        return _runtimeTransactions.TryRequestAppraisal(
            objectId,
            _sendExamine,
            AppraisalRequestOrigin.Automation);
    }

    public AppraisalResponseAcceptance AcceptAppraisalResponse(uint objectId)
    {
        bool ownedBusyReference = _transactions.AppraisalCount > 0;
        RuntimeAppraisalResponseAcceptance acceptance =
            _runtimeTransactions.AcceptAppraisalResponse(objectId);
        if (acceptance.FirstResponse && !ownedBusyReference)
            StateChanged?.Invoke();
        return new AppraisalResponseAcceptance(
            acceptance.Accepted,
            acceptance.FirstResponse,
            acceptance.Origin,
            acceptance.PresentInUi);
    }

    public bool RefreshCurrentAppraisal()
    {
        if (_sendExamine is null)
            return false;
        return _runtimeTransactions.RefreshCurrentAppraisal(_sendExamine);
    }

    public void CancelObjectAppraisalForSpell()
    {
        if (_sendExamine is null)
            return;
        bool ownedBusyReference = _transactions.AppraisalCount > 0;
        if (_runtimeTransactions.CancelObjectAppraisalForSpell(_sendExamine)
            && !ownedBusyReference)
        {
            StateChanged?.Invoke();
        }
    }

    public bool IsToolbarUseEnabled(uint selectedObjectId)
    {
        if (selectedObjectId == 0
            || _objects.Get(selectedObjectId) is not { } item)
            return false;

        return ItemInteractionPolicy.IsToolbarUseEnabled(
            item.Type,
            item.CombatUse ?? 0,
            item.Useability ?? ItemUseability.Undef);
    }

    public bool ActivateItem(uint itemGuid)
    {
        if (itemGuid == 0) return false;

        if (IsTargetModeActive)
            return OfferPrimaryClick(itemGuid) == ItemPrimaryClickResult.ConsumedSuccess;

        var item = _objects.Get(itemGuid);
        if (item is null) return false;

        if (!ConsumeUseThrottle())
            return true;
        if (!EnsureInventoryRequestReady())
            return false;

        var input = new ItemUsePolicyInput(
            Snapshot(item),
            _playerGuid(),
            _groundObjectId(),
            CanMakeInventoryRequest,
            _activeVendorId(),
            BypassClassification: false,
            UseCurrentSelection: false,
            SelectedTarget: null,
            ConfirmVolatileRareUses: _confirmVolatileRareUses(),
            InNonCombatMode: _inNonCombatMode());
        var decision = ItemInteractionPolicy.DecideUse(input);
        return ExecuteUseActions(decision.Actions);
    }

    /// <summary>
    /// Arms ExternalContainers.RequestOpen for objectId if -- and only if
    /// -- the same use policy a click evaluates would itself open it as a
    /// landscape container (a corpse, chest, or similar not-owned,
    /// useable, non-targeted container -- the exact predicate
    /// ItemInteractionPolicy's SetGroundObject action already encodes).
    /// This does not send anything and does not touch the current
    /// selection; it exists so the automation walk-then-use path
    /// (SelectionInteractionController.TryUseForAutomation) can arm the
    /// same seam a click's own SetGroundObject action arms before its
    /// approach-gated Use dispatches. Without this, the automation route
    /// dispatched the same wire Use a click does but left
    /// RequestedContainerId at 0, so the server's ViewContents response
    /// arrived with nothing armed to receive it and
    /// ExternalContainerState.ApplyViewContents silently dropped it --
    /// the plugin's Use reported Started and the container never opened.
    /// </summary>
    public void ArmLandscapeContainerRequest(uint objectId)
    {
        if (objectId == 0u || _objects.Get(objectId) is not { } item)
            return;

        // ReadyForInventoryRequest is hardcoded true here rather than
        // read live: this is called at the point Use is already being
        // dispatched, after the caller's own busy/reservation gate has
        // already been satisfied (and usually while that reservation is
        // itself held) -- re-reading the live busy state here would see
        // that same reservation and refuse to produce any actions at
        // all, including the one this method exists to run. This method
        // only re-derives "is this the kind of use that opens a
        // landscape container", not "is a request allowed right now".
        var input = new ItemUsePolicyInput(
            Snapshot(item),
            _playerGuid(),
            _groundObjectId(),
            ReadyForInventoryRequest: true,
            _activeVendorId(),
            BypassClassification: false,
            UseCurrentSelection: false,
            SelectedTarget: null,
            ConfirmVolatileRareUses: true,
            InNonCombatMode: _inNonCombatMode());
        ItemUsePolicyDecision decision = ItemInteractionPolicy.DecideUse(input);
        foreach (ItemPolicyAction action in decision.Actions)
        {
            if (action.Kind == ItemPolicyActionKind.SetGroundObject)
                _requestExternalContainer?.Invoke(action.ObjectId);
        }
    }

    /// <summary>
    /// What a plain use of an object means before any use request is
    /// considered: picking a loose or looted item up, wielding or sorting it,
    /// or nothing but the use itself. Null when the object is not known.
    /// </summary>
    public ItemPrimaryUseResult? ClassifyPrimaryUse(uint objectId) =>
        objectId == 0u || _objects.Get(objectId) is not { } item
            ? null
            : ItemInteractionPolicy.DetermineUseResult(
                Snapshot(item),
                _playerGuid(),
                _groundObjectId());

    public bool TryUseItemForAutomation(uint itemGuid)
    {
        if (itemGuid == 0u || _objects.Get(itemGuid) is not { } item)
            return false;
        if (ItemUseability.IsTargeted(item.Useability ?? ItemUseability.Undef))
            return false;
        if (!ConsumeUseThrottle())
            return false;
        if (!EnsureInventoryRequestReady())
            return false;

        var input = new ItemUsePolicyInput(
            Snapshot(item),
            _playerGuid(),
            _groundObjectId(),
            CanMakeInventoryRequest,
            _activeVendorId(),
            BypassClassification: true,
            UseCurrentSelection: false,
            SelectedTarget: null,
            ConfirmVolatileRareUses: _confirmVolatileRareUses(),
            InNonCombatMode: _inNonCombatMode());
        ItemUsePolicyDecision decision = ItemInteractionPolicy.DecideUse(input);
        bool sends = decision.Actions.Any(static action =>
            action.Kind == ItemPolicyActionKind.SendUse);
        return sends && ExecuteUseActions(decision.Actions);
    }

    public bool TryApplyItem(uint itemGuid, uint targetGuid)
    {
        if (itemGuid == 0u || targetGuid == 0u)
            return false;
        if (_objects.Get(itemGuid) is not { } item
            || _objects.Get(targetGuid) is not { } target)
        {
            return false;
        }
        if (!ConsumeUseThrottle())
            return true;
        if (!EnsureInventoryRequestReady())
            return false;

        var input = new ItemUsePolicyInput(
            Snapshot(item),
            _playerGuid(),
            _groundObjectId(),
            CanMakeInventoryRequest,
            _activeVendorId(),
            BypassClassification: true,
            UseCurrentSelection: true,
            SelectedTarget: Snapshot(target),
            ConfirmVolatileRareUses: _confirmVolatileRareUses(),
            InNonCombatMode: _inNonCombatMode());
        ItemUsePolicyDecision decision = ItemInteractionPolicy.DecideUse(input);
        bool sends = decision.Actions.Any(static action =>
            action.Kind == ItemPolicyActionKind.SendUseWithTarget);
        return sends && ExecuteUseActions(decision.Actions);
    }

    /// <summary>
    /// Primary use of an item the caller has already classified, with the
    /// current world selection standing in as the target. This is the entry
    /// the magic panel's wielded-caster slot uses: the caster is already
    /// wielded, so the auto-wield / auto-sort / place-in-pack classification
    /// step is skipped, and the item's own useability decides whether the
    /// request carries the selected target or goes out bare. A caster that
    /// carries a spell is authored source-wielded / target-remote, so this is
    /// what turns the slot into a targeted request the server answers with the
    /// caster's own spell, instead of a bare use it has no meaning for.
    /// </summary>
    public bool UseWithCurrentSelection(uint itemGuid)
    {
        if (itemGuid == 0u || _objects.Get(itemGuid) is not { } item)
            return false;
        if (!ConsumeUseThrottle())
            return true;
        if (!EnsureInventoryRequestReady())
            return false;

        uint selectedId = _selectedObjectId();
        ItemPolicyObject? selected =
            selectedId != 0u && _objects.Get(selectedId) is { } selectedItem
                ? Snapshot(selectedItem)
                : null;

        var input = new ItemUsePolicyInput(
            Snapshot(item),
            _playerGuid(),
            _groundObjectId(),
            CanMakeInventoryRequest,
            _activeVendorId(),
            BypassClassification: true,
            UseCurrentSelection: true,
            SelectedTarget: selected,
            ConfirmVolatileRareUses: _confirmVolatileRareUses(),
            InNonCombatMode: _inNonCombatMode());
        return ExecuteUseActions(ItemInteractionPolicy.DecideUse(input).Actions);
    }

    /// <summary>The pack a request without a named destination should name: the
    /// main pack when asked or preferred, the open side pack otherwise.</summary>
    public uint PreferredBackpackContainer(bool mainPack = false)
    {
        uint root = _playerGuid();
        uint target = mainPack || _mainPackPreferred() ? root : _backpackContainerId();
        return target == 0u ? root : target;
    }

    /// <summary>
    /// Picks up a world item into the inventory. A quantity that joins a
    /// stack the character already holds is sent as that join and needs no
    /// free slot. Otherwise the pack the request names is the one the player
    /// has open (or the main pack when asked); when it has no room the item
    /// goes to the main pack, then to the first side pack with room, and when
    /// nothing has room the "completely full" notice is shown and nothing is
    /// sent. The server fills exactly the pack it is asked for, so the choice
    /// must be made here.
    ///
    /// Answers true when the request was the client's to handle, which is not
    /// the same as it having been sent: the three ways it can come to nothing
    /// all answer true here, because a caller driving the inventory panel has
    /// already been told through the notice. A caller that has to act on the
    /// outcome -- automation, which has no one to read a notice -- asks
    /// <see cref="TryPlaceWorldItemInBackpack"/> instead.
    /// </summary>
    public bool PlaceWorldItemInBackpack(uint itemGuid, bool mainPack = false) =>
        TryPlaceWorldItemInBackpack(itemGuid, mainPack)
            != RuntimeBackpackPlacementOutcome.NotThisClients;

    /// <summary>
    /// The same placement, saying which of its endings it reached. Four of
    /// them send nothing, and a caller that cannot read a notice needs to
    /// tell them apart from the one that does.
    /// </summary>
    public RuntimeBackpackPlacementOutcome TryPlaceWorldItemInBackpack(
        uint itemGuid,
        bool mainPack = false)
    {
        if (itemGuid == 0u || _placeInBackpack is null)
            return RuntimeBackpackPlacementOutcome.NotThisClients;

        uint root = _playerGuid();
        uint target = PreferredBackpackContainer(mainPack);
        const int placement = 0;

        uint containerId = InventoryPlacementSearch.ChooseContainer(
            _objects, itemGuid, root, target, root,
            out InventoryContainerPlacementRejection noRoom);

        // Adding to a stack the character already holds costs no slot, so
        // whether one is free is not what decides it: the join is settled
        // before the room refusal, and a completely full inventory still
        // takes what only joins a stack inside it.
        if (TryPlanAutoMerge(itemGuid) is { } merge)
        {
            return TryDispatchPendingBackpackPlacement(
                    itemGuid,
                    containerId != 0u
                        ? containerId
                        : ContainerHolding(merge.TargetObjectId, root),
                    placement,
                    InventoryRequestKind.Merge,
                    () =>
                    {
                        _sendStackableMerge!(
                            merge.SourceObjectId,
                            merge.TargetObjectId,
                            merge.Amount);
                        MergeAttempted?.Invoke(
                            merge.SourceObjectId,
                            merge.TargetObjectId);
                        return true;
                    })
                ? RuntimeBackpackPlacementOutcome.Sent
                : RuntimeBackpackPlacementOutcome.NotDispatched;
        }

        if (containerId == 0u)
        {
            if (InventoryContainerPlacementPolicy.ComposeClientLocal(
                    noRoom, _objects.Get(itemGuid), _objects.Get(root), root) is { } fullNotice)
            {
                ReportClientLocal(fullNotice);
            }
            return RuntimeBackpackPlacementOutcome.NoRoom;
        }

        if (!TryBeginPendingBackpackPlacement(
                itemGuid,
                containerId,
                placement,
                out _))
        {
            return RuntimeBackpackPlacementOutcome.AlreadyPending;
        }
        _placeInBackpack(itemGuid, containerId, placement);
        return RuntimeBackpackPlacementOutcome.Sent;
    }

    /// <summary>
    /// The pack a merged quantity ends up in: the one holding the stack it
    /// joins. Falls back to the root when that stack's pack is not known,
    /// because the placement has to name a pack to wait on.
    /// </summary>
    private uint ContainerHolding(uint targetId, uint root)
    {
        uint container = _objects.Get(targetId)?.ContainerId ?? 0u;
        return container != 0u ? container : root;
    }

    private StackMergePlan? TryPlanAutoMerge(uint sourceId)
    {
        if (_objects.Get(sourceId) is not { } source)
            return null;
        uint requested = _stackSplitQuantity?.GetObjectSplitSize(
                sourceId,
                _selectedObjectId(),
                (uint)Math.Max(1, source.StackSize))
            ?? (uint)Math.Max(1, source.StackSize);
        uint player = _playerGuid();
        return player == 0u ? null : TryPlanAutoMerge(sourceId, player, requested);
    }

    /// <summary>
    /// The first stack in <paramref name="containerId"/> that can take all
    /// <paramref name="requested"/> of the source in one merge, searching the
    /// container's own items first and then each pack inside it. A stack
    /// that could take only part of it is passed over.
    /// </summary>
    private StackMergePlan? TryPlanAutoMerge(
        uint sourceId,
        uint containerId,
        uint requested)
    {
        if (_sendStackableMerge is null
            || containerId == 0u
            || _objects.Get(sourceId) is not { } source
            || source.StackSizeMax <= 1)
        {
            return null;
        }

        int requestedAmount = (int)Math.Min(requested, int.MaxValue);
        var sourceMerge = ToStackMergeItem(source);
        foreach (uint targetId in ExhaustiveContainedItems(containerId))
        {
            if (_objects.Get(targetId) is not { } target)
                continue;
            StackMergePlan? plan = StackMergePlanner.Plan(
                sourceMerge,
                ToStackMergeItem(target),
                CanMakeInventoryRequest,
                requestedAmount);
            if (plan is { } complete && complete.Amount == requested)
                return complete;
        }
        return null;
    }

    /// <summary>
    /// Everything a container holds, for finding a stack to join: its own
    /// items in order, then the items of each pack inside it, pack by pack.
    /// </summary>
    private IEnumerable<uint> ExhaustiveContainedItems(uint containerId) =>
        ExhaustiveContainedItems(containerId, new HashSet<uint>());

    private IEnumerable<uint> ExhaustiveContainedItems(
        uint containerId,
        HashSet<uint> visitedContainers)
    {
        if (!visitedContainers.Add(containerId))
            yield break;
        var packs = new List<uint>();
        foreach (uint itemId in _objects.GetContents(containerId))
        {
            if (_objects.Get(itemId) is { } held
                && InventoryContainerPlacementPolicy.IsContainer(held))
            {
                packs.Add(itemId);
                continue;
            }
            yield return itemId;
        }
        foreach (uint pack in packs)
        {
            foreach (uint itemId in ExhaustiveContainedItems(pack, visitedContainers))
                yield return itemId;
        }
    }

    private static StackMergeItem ToStackMergeItem(ClientObject item) => new(
        item.ObjectId,
        item.WeenieClassId,
        item.StackSize,
        item.StackSizeMax,
        item.TradeState);

    public bool TryBeginPendingBackpackPlacement(
        uint itemGuid,
        uint containerId,
        int placement,
        out PendingBackpackPlacement pending)
        => TryBeginPendingBackpackPlacement(
            itemGuid,
            containerId,
            placement,
            InventoryRequestKind.Pickup,
            out pending);

    private bool TryBeginPendingBackpackPlacement(
        uint itemGuid,
        uint containerId,
        int placement,
        InventoryRequestKind kind,
        out PendingBackpackPlacement pending)
    {
        if (itemGuid == 0u || containerId == 0u)
        {
            pending = default;
            return false;
        }

        if (_pendingBackpackPlacement is { } existing)
        {
            ReportPendingBackpackPlacementConflict();
            pending = existing;
            return false;
        }
        if (!EnsureInventoryRequestReady())
        {
            pending = default;
            return false;
        }

        PendingBackpackPlacement candidate = default;
        bool reserved;
        PendingInventoryRequest published;
        try
        {
            reserved = _transactions.TryReserve(
                kind,
                itemGuid,
                out published,
                request =>
                {
                    candidate = new PendingBackpackPlacement(
                        request.Token,
                        itemGuid,
                        containerId,
                        placement,
                        request.ItemIdentity);
                    _pendingBackpackPlacement = candidate;
                    List<Exception> failures = [];
                    DispatchAll(
                        PendingBackpackPlacementRequested,
                        candidate,
                        failures);
                    if (failures.Count != 0)
                    {
                        throw new AggregateException(
                            "One or more pending-placement observers failed.",
                            failures);
                    }
                });
        }
        catch (Exception failure)
        {
            if (candidate.Token == 0u)
                throw;

            _pendingBackpackPlacement = null;
            _transactions.CancelBeforeDispatch(candidate.Token);
            List<Exception> failures = [failure];
            DispatchAll(
                PendingBackpackPlacementCancelled,
                candidate,
                failures);
            throw new AggregateException(
                "Pending backpack placement publication failed.",
                failures);
        }
        pending = candidate;
        if (!reserved
            || _pendingBackpackPlacement != candidate
            || published.Token != candidate.Token
            || published.ItemId != itemGuid
            || published.Kind != kind
            || published.Dispatched)
        {
            return false;
        }
        return true;
    }

    public bool TryDispatchPendingBackpackPlacement(
        uint itemGuid,
        uint containerId,
        int placement,
        InventoryRequestKind kind,
        Func<bool> dispatch)
    {
        if (!EnsureInventoryRequestReady())
            return false;
        if (!TryBeginPendingBackpackPlacement(
                itemGuid,
                containerId,
                placement,
                kind,
                out PendingBackpackPlacement pending))
        {
            return false;
        }

        if (_pendingBackpackPlacement != pending
            || !_transactions.TryGetPending(out PendingInventoryRequest reserved)
            || reserved.Token != pending.Token
            || reserved.ItemId != pending.ItemId
            || reserved.Kind != kind
            || reserved.Dispatched)
        {
            return false;
        }

        if (TryDispatchInventoryRequest(
                kind,
                itemGuid,
                dispatch,
                pending.Token))
            return true;

        CancelPendingBackpackPlacement(itemGuid, pending.Token);
        return false;
    }

    public bool TryGetPendingBackpackPlacement(
        uint itemGuid,
        out PendingBackpackPlacement pending)
    {
        if (_pendingBackpackPlacement is { } current
            && current.ItemId == itemGuid)
        {
            pending = current;
            return true;
        }

        pending = default;
        return false;
    }

    public void CancelPendingBackpackPlacement(uint itemGuid = 0u, ulong token = 0u)
    {
        if (_pendingBackpackPlacement is not { } pending
            || (itemGuid != 0u && pending.ItemId != itemGuid)
            || (token != 0u && pending.Token != token))
        {
            return;
        }

        _pendingBackpackPlacement = null;
        _transactions.CancelBeforeDispatch(pending.Token);
        List<Exception> failures = [];
        DispatchAll(PendingBackpackPlacementCancelled, pending, failures);
        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more pending-placement cancellation observers failed.",
                failures);
        }
    }

    internal bool TryCaptureObjectIdentity(uint objectId, out ClientObject item)
    {
        item = _objects.Get(objectId)!;
        return item is not null;
    }

    internal bool IsInCurrentGroundObject(uint objectId)
    {
        uint groundObjectId = _groundObjectId();
        return groundObjectId != 0u
            && _objects.Get(objectId) is { } item
            && item.ContainerId == groundObjectId;
    }

    internal bool IsCurrentObjectIdentity(uint objectId, ClientObject item)
        => ReferenceEquals(_objects.Get(objectId), item);

    public bool WieldFromPaperdoll(uint itemGuid, EquipMask targetMask)
    {
        if (itemGuid == 0u || targetMask == EquipMask.None)
            return false;
        if (_objects.Get(itemGuid) is not { } item)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;
        return _autoWield.TryWield(item, targetMask);
    }

    /// <summary>
    /// Plugin/automation entry into the exact same AutoWield transaction used
    /// by inventory activation and paperdoll drops.
    /// </summary>
    public bool TryWieldItem(uint itemGuid, EquipMask requestedMask = EquipMask.None)
    {
        if (itemGuid == 0u || _objects.Get(itemGuid) is not { } item)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;
        return requestedMask == EquipMask.None
            ? _autoWield.TryWield(item)
            : _autoWield.TryWield(item, requestedMask);
    }

    public bool TryWieldItemSecondary(uint itemGuid)
    {
        if (itemGuid == 0u || _objects.Get(itemGuid) is not { } item)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;
        return _autoWield.TryWieldSecondary(item);
    }

    /// <summary>
    /// Whether an equipment switch is in flight -- that and nothing else.
    /// It once also answered true whenever ANY inventory request was
    /// pending, which made it true almost continuously for an automation
    /// that appraises as it goes, and every consumer read that as "the
    /// character is mid-wield". A wield attempted while a request is
    /// pending is refused at dispatch anyway, so the readiness check
    /// belongs there and not in this signal.
    /// </summary>
    public bool IsAutoWieldBusy => _autoWield.IsBusy;

    public void NotifyExplicitCombatModeRequest()
        => _autoWield.NotifyExplicitCombatModeRequest();

    public bool AcquireTarget(uint targetGuid)
    {
        if (!IsTargetModeActive || targetGuid == 0) return false;

        uint sourceGuid = PendingSourceItem;
        ClearTargetMode();

        var source = _objects.Get(sourceGuid);
        if (source?.Useability is not { } useability)
            return false;

        bool compatible = TargetCompatible(source, targetGuid);
        var target = _objects.Get(targetGuid);
        Console.WriteLine(
            $"[use-target] src=0x{sourceGuid:X8} use=0x{useability:X8} ttypeMask=0x{source.TargetType ?? 0u:X8}"
            + $" tgt=0x{targetGuid:X8} tgtKind={(target is null ? "none" : $"0x{(uint)target.Type:X8}")}"
            + $" -> {(compatible ? "SEND UseWithTarget" : "refused")}");
        if (!compatible)
            return false;

        if (!EnsureInventoryRequestReady())
            return false;
        _runtimeTransactions.TryDispatchTargetedUse(
            sourceGuid,
            targetGuid,
            _sendUseWithTarget,
            incrementBusy: true);
        return true;
    }

    public bool AcquireSelfTarget()
        => IsTargetModeActive && AcquireTarget(_playerGuid());

    public bool ExecuteConfirmedUse(uint objectId)
    {
        if (objectId == 0u || (_requestUse is null && _sendUse is null))
            return false;
        if (!EnsureInventoryRequestReady())
            return false;
        if (_requestUse is not null)
        {
            ItemUseRequestReservation reservation = BeginUseRequestReservation();
            try
            {
                _requestUse(objectId, reservation);
            }
            catch
            {
                reservation.CancelBeforeDispatch();
                throw;
            }
        }
        else
        {
            _sendUse!(objectId);
            _runtimeTransactions.IncrementBusyCount();
        }
        return true;
    }

    public void CancelTargetMode()
    {
        if (!IsAnyTargetModeActive) return;
        ClearTargetMode();
    }

    /// <summary>
    /// True when <paramref name="targetGuid"/> is a live, other-player
    /// object (the "Player" weenie flag, and not this client's own
    /// player). Shared by TryOpenSecureTradeWithPlayer and the
    /// world-object automation path, which needs to recognize a player
    /// target WITHOUT the side effect of opening a trade -- a plugin's
    /// Use on another player should be refused, not silently start one.
    /// </summary>
    public bool IsPlayerTarget(uint targetGuid)
    {
        if (targetGuid == 0u || targetGuid == _playerGuid())
            return false;
        ClientObject? target = _objects.Get(targetGuid);
        return target is not null
            && ((PublicWeenieFlags)(target.PublicWeenieBitfield ?? 0u)
                & PublicWeenieFlags.Player) != 0;
    }

    public bool TryOpenSecureTradeWithPlayer(uint targetGuid)
    {
        if (!IsPlayerTarget(targetGuid))
            return false;

        if (_inNonCombatMode())
            SecureTradeRequested?.Invoke(targetGuid, 0u);
        return true;
    }

    public bool PlaceSelectedIn3D(uint itemGuid, uint targetGuid)
        => PlaceIn3D(itemGuid, fromShortcutBar: false, targetGuid);

    /// <summary>
    /// Places an item into the world, either on the ground or onto another
    /// object. A shortcut-bar slot holds an alias, not the item, so nothing
    /// can be placed out of one.
    /// </summary>
    public bool PlaceIn3D(
        uint itemGuid,
        bool fromShortcutBar,
        uint targetGuid)
    {
        if (fromShortcutBar)
            return false;
        if (itemGuid == 0 || _objects.Get(itemGuid) is not { } item)
            return false;
        if (!EnsureInventoryRequestReady())
            return false;

        uint fullStack = (uint)Math.Max(1, item.StackSize);
        int splitSize = (int)(_stackSplitQuantity?.GetObjectSplitSize(
            item.ObjectId, _selectedObjectId(), fullStack) ?? fullStack);
        ClientObject? target = targetGuid == 0u ? null : _objects.Get(targetGuid);
        var decision = ItemInteractionPolicy.DecidePlacement(new ItemPlacementPolicyInput(
            Snapshot(item),
            _playerGuid(),
            _groundObjectId(),
            CanMakeInventoryRequest,
            TargetId: targetGuid,
            Target: target is null ? null : Snapshot(target),
            AllowGroundFallback: true,
            MergeAccepted: false,
            DragOnPlayerOpensSecureTrade: _dragOnPlayerOpensSecureTrade(),
            PlayerOnGround: _playerOnGround(),
            SplitSize: splitSize));
        ExecutePlacementActions(decision.Actions);
        return decision.ReturnValue;
    }

    private bool ExecuteUseActions(System.Collections.Generic.IReadOnlyList<ItemPolicyAction> actions)
    {
        uint openedOrUsed = 0;
        bool acted = false;
        bool busyOwnedByUseReservation = false;
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case ItemPolicyActionKind.PlaceInBackpack:
                    acted |= PlaceWorldItemInBackpack(action.ObjectId);
                    break;
                case ItemPolicyActionKind.WieldRight:
                case ItemPolicyActionKind.WieldLeft:
                case ItemPolicyActionKind.AutoSort:
                    if (_objects.Get(action.ObjectId) is { } item)
                        acted |= _autoWield.TryWield(item);
                    break;
                case ItemPolicyActionKind.OpenContainedContainer:
                case ItemPolicyActionKind.SendUse:
                    if (openedOrUsed != action.ObjectId)
                    {
                        if (_requestUse is not null)
                        {
                            ItemUseRequestReservation reservation =
                                BeginUseRequestReservation();
                            try
                            {
                                _requestUse(action.ObjectId, reservation);
                            }
                            catch
                            {
                                reservation.CancelBeforeDispatch();
                                throw;
                            }
                            busyOwnedByUseReservation = true;
                        }
                        else
                        {
                            _sendUse?.Invoke(action.ObjectId);
                        }
                        openedOrUsed = action.ObjectId;
                        acted |= _requestUse is not null || _sendUse is not null;
                    }
                    break;
                case ItemPolicyActionKind.SendUseWithTarget:
                    acted |= _runtimeTransactions.TryDispatchTargetedUse(
                        action.ObjectId,
                        action.TargetId,
                        _sendUseWithTarget,
                        incrementBusy: false);
                    break;
                case ItemPolicyActionKind.SetGroundObject:
                    _requestExternalContainer?.Invoke(action.ObjectId);
                    acted |= _requestExternalContainer is not null;
                    break;
                case ItemPolicyActionKind.EnterTargetMode:
                    EnterTargetMode(action.ObjectId);
                    acted = true;
                    break;
                case ItemPolicyActionKind.IncrementBusy:
                    if (busyOwnedByUseReservation)
                    {
                        busyOwnedByUseReservation = false;
                    }
                    else
                    {
                        _runtimeTransactions.IncrementBusyCount();
                    }
                    break;
                case ItemPolicyActionKind.Reject:
                    if (!string.IsNullOrWhiteSpace(action.Message))
                        ReportClientLocal(action.Message);
                    break;
                case ItemPolicyActionKind.OpenSecureTrade:
                    SecureTradeRequested?.Invoke(action.ObjectId, 0u);
                    acted |= SecureTradeRequested is not null;
                    break;
                default:
                    _auxiliaryAction?.Invoke(action);
                    PolicyActionRequested?.Invoke(action);
                    bool handled = _auxiliaryAction is not null || PolicyActionRequested is not null;
                    if (!handled)
                        ReportClientLocal(PolicyActionMessage(action));
                    acted |= handled || _interfaceText is not null
                        || _systemMessage is not null || _toast is not null;
                    break;
            }
        }
        return acted;
    }

    private ItemUseRequestReservation BeginUseRequestReservation()
        => _runtimeTransactions.BeginUseRequestReservation();

    /// <summary>
    /// The same use reservation every inventory-item automation entry
    /// point takes (TryUseItemForAutomation and friends) before dispatch,
    /// exposed for the world-object automation path
    /// (SelectionInteractionController.TryUseForAutomation) so a
    /// non-owned target's busy-count/cancel-on-failure bookkeeping matches
    /// a click exactly instead of being skipped.
    /// </summary>
    public ItemUseRequestReservation BeginAutomationUseReservation()
        => BeginUseRequestReservation();

    private void ExecutePlacementActions(System.Collections.Generic.IReadOnlyList<ItemPolicyAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case ItemPolicyActionKind.PlaceInBackpack:
                    PlaceWorldItemInBackpack(action.ObjectId);
                    break;
                case ItemPolicyActionKind.StartSecureTrade:
                    SecureTradeRequested?.Invoke(action.TargetId, action.ObjectId);
                    break;
                case ItemPolicyActionKind.DropToWorld:
                    TryDispatchInventoryRequest(
                        InventoryRequestKind.DropToWorld,
                        action.ObjectId,
                        () =>
                        {
                            if (_sendDrop is null)
                                return false;
                            _sendDrop(action.ObjectId);
                            return true;
                        });
                    break;
                case ItemPolicyActionKind.SplitToWorld:
                    TryDispatchInventoryRequest(
                        InventoryRequestKind.SplitToWorld,
                        action.ObjectId,
                        () =>
                        {
                            if (_sendSplitToWorld is null)
                                return false;
                            _sendSplitToWorld(action.ObjectId, (uint)action.Amount);
                            if (_transactions.TryGetPending(out PendingInventoryRequest pending)
                                && pending.Kind is InventoryRequestKind.SplitToWorld
                                && pending.ItemId == action.ObjectId)
                            {
                                WorldDropDispatched?.Invoke(new WorldDropDispatch(
                                    pending,
                                    (uint)action.Amount));
                            }
                            return true;
                        });
                    break;
                case ItemPolicyActionKind.GiveToTarget:
                    TryDispatchInventoryRequest(
                        InventoryRequestKind.Give,
                        action.ObjectId,
                        () =>
                        {
                            if (_sendGive is null)
                                return false;
                            _sendGive(
                                action.TargetId,
                                action.ObjectId,
                                (uint)Math.Max(1, action.Amount));
                            return true;
                        });
                    break;
                case ItemPolicyActionKind.PlaceInContainer:
                {
                    uint fullStack = (uint)Math.Max(
                        1,
                        _objects.Get(action.ObjectId)?.StackSize ?? action.Amount);
                    uint amount = (uint)Math.Max(1, action.Amount);
                    InventoryRequestKind kind = amount < fullStack
                        ? InventoryRequestKind.SplitToContainer
                        : InventoryRequestKind.PutInContainer;
                    TryDispatchInventoryRequest(
                        kind,
                        action.ObjectId,
                        () =>
                        {
                            if (amount < fullStack)
                            {
                                if (_sendSplitToContainer is null)
                                    return false;
                                _sendSplitToContainer(
                                    action.ObjectId,
                                    action.TargetId,
                                    0u,
                                    amount);
                            }
                            else
                            {
                                if (_sendPutItemInContainer is null)
                                    return false;
                                _sendPutItemInContainer(
                                    action.ObjectId,
                                    action.TargetId,
                                    0);
                            }
                            return true;
                        });
                    break;
                }
                case ItemPolicyActionKind.Reject:
                    if (!string.IsNullOrWhiteSpace(action.Message))
                        ReportClientLocal(action.Message);
                    break;
                default:
                    _auxiliaryAction?.Invoke(action);
                    PolicyActionRequested?.Invoke(action);
                    if (_auxiliaryAction is null && PolicyActionRequested is null)
                        ReportClientLocal(PolicyActionMessage(action));
                    break;
            }
        }
    }

    private void EnterTargetMode(uint sourceGuid)
    {
        _consumedPrimaryClickTarget = 0;
        _interactionState.EnterUseItemOnTarget(sourceGuid);
        var name = _objects.Get(sourceGuid)?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            ReportClientLocal($"Choose a target for the {name}");
    }

    private void ClearTargetMode()
    {
        _interactionState.Clear();
    }

    private static string PolicyActionMessage(ItemPolicyAction action)
        => action.Kind switch
        {
            ItemPolicyActionKind.ConfirmPlayerKillerSwitch =>
                "Confirm using this Player Killer altar before continuing.",
            ItemPolicyActionKind.ConfirmNonPlayerKillerSwitch =>
                "Confirm using this Non-Player Killer altar before continuing.",
            ItemPolicyActionKind.ConfirmVolatileRare =>
                "Confirm using this volatile rare before continuing.",
            ItemPolicyActionKind.OpenSecureTrade or ItemPolicyActionKind.StartSecureTrade =>
                "Secure trade is not open.",
            ItemPolicyActionKind.OpenSalvage => "Open the salvage panel to use that item.",
            _ => "That item action is not available here.",
        };

    private void OnInteractionModeChanged(InteractionModeTransition _)
    {
        List<Exception> failures = [];
        DispatchAll(StateChanged, failures);
        if (failures.Count != 0)
            throw new AggregateException(
                "One or more item-interaction observers failed.",
                failures);
    }

    private void OnTransactionStateChanged()
    {
        List<Exception> failures = [];
        DispatchAll(StateChanged, failures);
        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more item-interaction observers failed.",
                failures);
        }
    }

    private void OnInventoryRequestCompleted(PendingInventoryRequest request)
    {
        if (_pendingBackpackPlacement is { } placement
            && placement.Token == request.Token
            && placement.ItemId == request.ItemId)
        {
            _pendingBackpackPlacement = null;
            List<Exception> failures = [];
            DispatchAll(PendingBackpackPlacementResolved, placement, failures);
            if (failures.Count != 0)
            {
                throw new AggregateException(
                    "One or more pending-placement resolution observers failed.",
                    failures);
            }
        }
    }

    private void OnInventoryObjectsCleared()
    {
        _pendingBackpackPlacement = null;
    }

    private void OnInventoryRequestFailed(
        PendingInventoryRequest request,
        uint weenieError)
    {
        ClientObject? item = request.ItemIdentity ?? _objects.Get(request.ItemId);
        if (item is null)
            return;
        bool plural = request.Kind
            is InventoryRequestKind.Merge
            or InventoryRequestKind.SplitToContainer
            or InventoryRequestKind.SplitToWorld;
        string name = plural && !string.IsNullOrEmpty(item.PluralName)
            ? item.PluralName
            : item.GetAppropriateName();
        if (string.IsNullOrEmpty(name))
            return;
        if (InventoryFailureMessages.Compose(request.Kind, name, weenieError)
            is { } text)
        {
            ReportClientLocal(text);
        }
    }

    private void OnMoveRequestFailedNotice(MoveRequestFailure failure)
    {
        if (_interfaceText is null
            || failure.WeenieError == 0u
            || InventoryFailureMessages.SuppressesGenericFailureText(
                failure.WeenieError))
        {
            return;
        }
        var (text, type) = WeenieErrorMessages.Resolve(failure.WeenieError, null);
        if (text is not null)
            _interfaceText(text, type);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _interactionState.Changed -= OnInteractionModeChanged;
        _objects.MoveRequestFailed -= OnMoveRequestFailedNotice;
        _transactions.ObjectTableCleared -= OnInventoryObjectsCleared;
        _transactions.RequestFailed -= OnInventoryRequestFailed;
        _transactions.RequestCompleted -= OnInventoryRequestCompleted;
        _transactions.StateChanged -= OnTransactionStateChanged;
        WorldDropDispatched = null;
        MergeAttempted = null;
        _autoWield.Dispose();
    }

    public void CompleteUse(uint error)
        => _runtimeTransactions.CompleteUse(error);

    public void ClearBusy()
        => _transactions.ClearBusy();

    public void ResetSession()
        => ResetSessionCore(resetRuntime: true);

    internal void ResetGenerationPresentation()
        => ResetSessionCore(resetRuntime: false);

    private void ResetSessionCore(bool resetRuntime)
    {
        PendingBackpackPlacement? pendingPlacement = _pendingBackpackPlacement;
        _pendingBackpackPlacement = null;
        if (resetRuntime)
        {
            _transactions.StateChanged -= OnTransactionStateChanged;
            try
            {
                _runtimeTransactions.ResetSession();
            }
            finally
            {
                if (!_disposed)
                    _transactions.StateChanged += OnTransactionStateChanged;
            }
        }
        _consumedPrimaryClickTarget = 0u;
        _consumedPrimaryClickMs = long.MinValue / 2;

        List<Exception> failures = [];
        if (resetRuntime)
        {
            try { _interactionState.ResetSession(); }
            catch (Exception error) { failures.Add(error); }
        }

        if (pendingPlacement is { } pending)
            DispatchAll(PendingBackpackPlacementCancelled, pending, failures);

        if (failures.Count != 0)
            throw new AggregateException(
                "One or more item-interaction reset observers failed.",
                failures);
    }

    /// <summary>
    /// Origin and PresentInUi are only meaningful when FirstResponse is
    /// true -- see RuntimeAppraisalResponseAcceptance, which this mirrors.
    /// </summary>
    public readonly record struct AppraisalResponseAcceptance(
        bool Accepted,
        bool FirstResponse,
        AppraisalRequestOrigin Origin,
        bool PresentInUi);

    private static void DispatchAll(Action? listeners, List<Exception> failures)
    {
        if (listeners is null)
            return;
        foreach (Action listener in listeners.GetInvocationList())
        {
            try { listener(); }
            catch (Exception error) { failures.Add(error); }
        }
    }

    private static void DispatchAll<T>(
        Action<T>? listeners,
        T value,
        List<Exception> failures)
    {
        if (listeners is null)
            return;
        foreach (Action<T> listener in listeners.GetInvocationList())
        {
            try { listener(value); }
            catch (Exception error) { failures.Add(error); }
        }
    }

    private bool ConsumeUseThrottle()
        => _runtimeTransactions.TryConsumeUseThrottle(_nowMs());

    /// <summary>
    /// The same use-throttle gate every inventory-item automation entry
    /// point applies, exposed for the world-object automation path so a
    /// non-owned target cannot bypass the throttle a click (or an owned
    /// item's automation) is held to.
    /// </summary>
    public bool TryConsumeUseThrottleForAutomation() => ConsumeUseThrottle();

    /// <summary>
    /// Whether that same gate would let a use through right now. Asking does
    /// not spend the stamp, so an automation entry point can answer "not yet,
    /// ask again next frame" instead of reporting a refusal its caller would
    /// count as a failed attempt.
    /// </summary>
    public bool IsUseThrottleReadyForAutomation =>
        _runtimeTransactions.IsUseThrottleReady(_nowMs());

    private static bool IsContainer(ClientObject item)
        => item.ContainerTypeHint != 0
        || item.Type.HasFlag(ItemType.Container)
        || item.ItemsCapacity > 0;

    private ItemPolicyObject Snapshot(ClientObject item)
    {
        var flags = (PublicWeenieFlags)(item.PublicWeenieBitfield ?? 0u);
        if (item.ObjectId == _playerGuid())
            flags |= PublicWeenieFlags.Player;

        bool owned = item.ObjectId == _playerGuid()
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
            IsIn3DView: item.ContainerId == 0 && item.WielderId == 0
                && item.ObjectId != _playerGuid(),
            Name: item.GetAppropriateName());
    }

    private bool TargetCompatible(ClientObject source, uint targetGuid)
    {
        var target = _objects.Get(targetGuid);
        if (target is null)
            return false;
        return ItemInteractionPolicy.IsTargetCompatible(
            Snapshot(source), Snapshot(target), _playerGuid());
    }

    private bool IsEquippedByPlayer(ClientObject item)
    {
        uint player = _playerGuid();
        return item.CurrentlyEquippedLocation != EquipMask.None
            && (item.WielderId == player || item.ContainerId == player);
    }

    private bool IsCarriedByPlayer(ClientObject item)
    {
        uint player = _playerGuid();
        uint container = item.ContainerId;
        for (int hops = 0; container != 0 && hops < 8; hops++)
        {
            if (container == player) return true;
            container = _objects.Get(container)?.ContainerId ?? 0u;
        }
        return false;
    }
}
