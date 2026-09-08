using AcDream.Core.Items;

namespace AcDream.Runtime.Gameplay;

public enum RuntimeQueuedInteractionKind
{
    Activate,
    Use,
    Pickup,
}

public readonly record struct RuntimeInteractionIdentity(
    uint ServerGuid,
    uint? LocalEntityId,
    ClientObject? ClientObject);

public readonly record struct RuntimeQueuedInteraction(
    RuntimeQueuedInteractionKind Kind,
    RuntimeInteractionIdentity Identity);

public readonly record struct RuntimeInteractionApproachToken(
    ulong ControllerLifetime,
    ulong ApproachGeneration);

public readonly record struct RuntimePendingPickup(
    ulong Token,
    uint ServerGuid,
    uint LocalEntityId,
    uint DestinationContainerId,
    int Placement,
    ulong PendingPlacementToken,
    RuntimeInteractionApproachToken ApproachToken);

public readonly record struct RuntimePendingUse(
    ulong Token,
    uint ServerGuid,
    bool OwnedByPlayer,
    bool Useable,
    ItemUseRequestReservation? Reservation,
    RuntimeInteractionApproachToken ApproachToken);

public readonly record struct RuntimeAppraisalResponseAcceptance(
    bool Accepted,
    bool FirstResponse);

public readonly record struct RuntimeItemUseCompletion(
    long Revision,
    uint SourceObjectId,
    uint TargetObjectId,
    uint WeenieError)
{
    public bool IsSuccess => Revision != 0 && WeenieError == 0u;
}

public enum RuntimeInteractionDispatchResult
{
    Rejected,
    NotInWorld,
    NotUseable,
    Dispatched,
}

public readonly record struct RuntimeInteractionTransactionSnapshot(
    bool IsDisposed,
    long Revision,
    uint LastUseSourceId,
    uint LastUseTargetId,
    uint AwaitingAppraisalId,
    uint CurrentAppraisalId,
    int OutboundCount,
    bool HasPendingPickup,
    ulong PendingPickupToken,
    long DispatchFailureCount,
    bool HasPendingUse = false,
    ulong PendingUseToken = 0u,
    bool AwaitingItemUseCompletion = false,
    RuntimeItemUseCompletion LastItemUseCompletion = default)
{
    public bool IsConverged =>
        IsDisposed
        && LastUseSourceId == 0u
        && LastUseTargetId == 0u
        && AwaitingAppraisalId == 0u
        && CurrentAppraisalId == 0u
        && OutboundCount == 0
        && !HasPendingPickup
        && !HasPendingUse
        && !AwaitingItemUseCompletion
        && LastItemUseCompletion.Revision == 0;
}

public sealed class RuntimeInteractionTransactionState : IDisposable
{
    public const long RetailUseThrottleMs = 200;

    private readonly InventoryTransactionState _inventory;
    private readonly Queue<RuntimeQueuedInteraction> _outbound = new();
    private long _lastUseMs = long.MinValue / 2;
    private uint _lastUseSourceId;
    private uint _lastUseTargetId;
    private uint _awaitingAppraisalId;
    private uint _currentAppraisalId;
    private RuntimePendingPickup? _pendingPickup;
    private ulong _nextPickupToken;
    private RuntimePendingUse? _pendingUse;
    private ulong _nextUseToken;
    private uint _clearEpoch;
    private long _revision;
    private long _dispatchFailureCount;
    private bool _awaitingItemUseCompletion;
    private bool _disposed;

    public RuntimeInteractionTransactionState(
        InventoryTransactionState inventory)
    {
        _inventory = inventory
            ?? throw new ArgumentNullException(nameof(inventory));
    }

    public InventoryTransactionState Inventory => _inventory;
    public uint AwaitingAppraisalId => _awaitingAppraisalId;
    public uint CurrentAppraisalId => _currentAppraisalId;
    public int OutboundCount => _outbound.Count;
    public bool HasPendingPickup => _pendingPickup is not null;
    public bool HasPendingUse => _pendingUse is not null;
    public bool IsDisposed => _disposed;
    public long Revision => Interlocked.Read(ref _revision);
    public long DispatchFailureCount =>
        Interlocked.Read(ref _dispatchFailureCount);
    public Exception? LastDispatchFailure { get; private set; }
    public RuntimeItemUseCompletion LastItemUseCompletion { get; private set; }

    public RuntimeInteractionTransactionSnapshot CaptureOwnership() => new(
        _disposed,
        Revision,
        _lastUseSourceId,
        _lastUseTargetId,
        _awaitingAppraisalId,
        _currentAppraisalId,
        _outbound.Count,
        _pendingPickup is not null,
        _pendingPickup?.Token ?? 0u,
        DispatchFailureCount,
        _pendingUse is not null,
        _pendingUse?.Token ?? 0u,
        _awaitingItemUseCompletion,
        LastItemUseCompletion);

    public bool TryConsumeUseThrottle(long nowMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nowMs - _lastUseMs < RetailUseThrottleMs)
            return false;
        _lastUseMs = nowMs;
        IncrementRevision();
        return true;
    }

    public ItemUseRequestReservation BeginUseRequestReservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inventory.BeginUseRequestReservation();
    }

    public RuntimeInteractionDispatchResult TryDispatchUse(
        uint serverGuid,
        bool ownedByPlayer,
        bool useable,
        ItemUseRequestReservation? reservation,
        IRuntimeInteractionTransport transport,
        out uint sequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transport);
        sequence = 0u;
        RuntimeInteractionDispatchResult verdict;

        if (serverGuid == 0u)
        {
            reservation?.CancelBeforeDispatch();
            verdict = RuntimeInteractionDispatchResult.Rejected;
        }
        else if (!transport.IsInWorld)
        {
            reservation?.CancelBeforeDispatch();
            verdict = RuntimeInteractionDispatchResult.NotInWorld;
        }
        else if (!ownedByPlayer && !useable)
        {
            reservation?.CancelBeforeDispatch();
            verdict = RuntimeInteractionDispatchResult.NotUseable;
        }
        else if (!transport.TrySendUse(serverGuid, out sequence))
        {
            reservation?.CancelBeforeDispatch();
            verdict = RuntimeInteractionDispatchResult.Rejected;
        }
        else
        {
            reservation?.MarkDispatched();
            _lastUseSourceId = serverGuid;
            _lastUseTargetId = 0u;
            _awaitingItemUseCompletion = true;
            IncrementRevision();
            verdict = RuntimeInteractionDispatchResult.Dispatched;
        }

        return verdict;
    }

    public bool TryDispatchTargetedUse(
        uint sourceObjectId,
        uint targetObjectId,
        Action<uint, uint>? dispatch,
        bool incrementBusy)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceObjectId == 0u
            || targetObjectId == 0u
            || dispatch is null)
            return false;

        uint epoch = _clearEpoch;
        dispatch(sourceObjectId, targetObjectId);
        if (_disposed || epoch != _clearEpoch)
            return false;

        _lastUseSourceId = sourceObjectId;
        _lastUseTargetId = targetObjectId;
        _awaitingItemUseCompletion = true;
        if (incrementBusy)
            _inventory.IncrementBusyCount();
        IncrementRevision();
        return true;
    }

    public void IncrementBusyCount()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inventory.IncrementBusyCount();
        IncrementRevision();
    }

    public void CompleteUse(uint error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int before = _inventory.BusyCount;
        _inventory.CompleteUse(error);
        if (_awaitingItemUseCompletion)
        {
            LastItemUseCompletion = new RuntimeItemUseCompletion(
                LastItemUseCompletion.Revision + 1,
                _lastUseSourceId,
                _lastUseTargetId,
                error);
            _awaitingItemUseCompletion = false;
            IncrementRevision();
        }
        else if (_inventory.BusyCount != before)
            IncrementRevision();
    }

    public bool TryRequestAppraisal(
        uint objectId,
        Action<uint> sendAppraisal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sendAppraisal);
        if (objectId == 0u)
            return false;

        uint epoch = _clearEpoch;
        bool acquiredBusy = _awaitingAppraisalId == 0u;
        if (acquiredBusy)
        {
            _inventory.IncrementBusyCount();
            if (_disposed || epoch != _clearEpoch)
                return false;
        }

        uint previousAwaiting = _awaitingAppraisalId;
        _awaitingAppraisalId = objectId;
        IncrementRevision();
        try
        {
            sendAppraisal(objectId);
        }
        catch
        {
            if (!_disposed
                && epoch == _clearEpoch
                && _awaitingAppraisalId == objectId)
            {
                _awaitingAppraisalId = previousAwaiting;
                if (acquiredBusy)
                    _inventory.CompleteUse(0u);
                IncrementRevision();
            }
            throw;
        }
        return true;
    }

    public RuntimeAppraisalResponseAcceptance AcceptAppraisalResponse(
        uint objectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (objectId == 0u
            || (objectId != _awaitingAppraisalId
                && objectId != _currentAppraisalId))
        {
            return default;
        }

        bool firstResponse = objectId == _awaitingAppraisalId;
        if (firstResponse)
        {
            _awaitingAppraisalId = 0u;
            _currentAppraisalId = objectId;
            _inventory.CompleteUse(0u);
            IncrementRevision();
        }

        return new RuntimeAppraisalResponseAcceptance(
            Accepted: true,
            FirstResponse: firstResponse);
    }

    public bool RefreshCurrentAppraisal(Action<uint> sendAppraisal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sendAppraisal);
        if (_currentAppraisalId == 0u)
            return false;
        sendAppraisal(_currentAppraisalId);
        return true;
    }

    public bool CancelObjectAppraisalForSpell(Action<uint> sendAppraisal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sendAppraisal);
        if (_awaitingAppraisalId == 0u && _currentAppraisalId == 0u)
            return false;

        if (_awaitingAppraisalId != 0u)
            _inventory.CompleteUse(0u);
        _awaitingAppraisalId = 0u;
        _currentAppraisalId = 0u;
        IncrementRevision();
        sendAppraisal(0u);
        return true;
    }

    public void Enqueue(RuntimeQueuedInteraction interaction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interaction.Identity.ServerGuid == 0u)
            return;
        _outbound.Enqueue(interaction);
        IncrementRevision();
    }

    public int CancelQueuedInteractions(
        uint serverGuid,
        uint? localEntityId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (serverGuid == 0u || _outbound.Count == 0)
            return 0;

        int original = _outbound.Count;
        int removed = 0;
        for (int i = 0; i < original; i++)
        {
            RuntimeQueuedInteraction interaction = _outbound.Dequeue();
            RuntimeInteractionIdentity identity = interaction.Identity;
            bool matches =
                identity.ServerGuid == serverGuid
                && (localEntityId is not uint exact
                    || identity.LocalEntityId == exact);
            if (matches)
                removed++;
            else
                _outbound.Enqueue(interaction);
        }
        if (removed != 0)
            IncrementRevision();
        return removed;
    }

    /// <summary>
    /// Drains only work present at the frame boundary. Re-entrant additions
    /// remain for the next frame, matching the pre-J5 ordered interaction
    /// queue without retaining App delegates.
    /// </summary>
    public void DrainOutbound(Action<RuntimeQueuedInteraction> dispatch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dispatch);

        int count = _outbound.Count;
        uint epoch = _clearEpoch;
        bool drained = false;
        while (count-- > 0 && epoch == _clearEpoch && _outbound.Count > 0)
        {
            RuntimeQueuedInteraction interaction = _outbound.Dequeue();
            drained = true;
            try
            {
                dispatch(interaction);
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref _dispatchFailureCount);
                LastDispatchFailure = error;
                throw;
            }
        }
        if (drained)
            IncrementRevision();
    }

    public bool TryArmPostArrivalPickup(
        uint serverGuid,
        uint localEntityId,
        uint destinationContainerId,
        int placement,
        ulong pendingPlacementToken,
        RuntimeInteractionApproachToken approachToken,
        out RuntimePendingPickup pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (serverGuid == 0u
            || localEntityId == 0u
            || destinationContainerId == 0u
            || pendingPlacementToken == 0u
            || approachToken.ControllerLifetime == 0u
            || approachToken.ApproachGeneration == 0u)
        {
            pending = default;
            return false;
        }
        if (_pendingPickup is not null)
        {
            pending = default;
            return false;
        }

        ulong token = ++_nextPickupToken;
        if (token == 0u)
            token = ++_nextPickupToken;
        pending = new RuntimePendingPickup(
            token,
            serverGuid,
            localEntityId,
            destinationContainerId,
            placement,
            pendingPlacementToken,
            approachToken);
        _pendingPickup = pending;
        IncrementRevision();
        return true;
    }

    public bool TryResolveApproachCompletion(
        RuntimeInteractionApproachToken approachToken,
        bool natural,
        out RuntimePendingPickup pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPickup is not { } current
            || current.ApproachToken != approachToken)
        {
            pending = default;
            return false;
        }

        _pendingPickup = null;
        pending = current;
        IncrementRevision();
        return natural;
    }

    public bool TryGetPendingPickup(out RuntimePendingPickup pending)
    {
        if (_pendingPickup is { } current)
        {
            pending = current;
            return true;
        }
        pending = default;
        return false;
    }

    public bool TryCancelPendingPickup(out RuntimePendingPickup pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPickup is not { } current)
        {
            pending = default;
            return false;
        }
        _pendingPickup = null;
        pending = current;
        IncrementRevision();
        return true;
    }

    public bool TryCancelPendingPickup(
        uint serverGuid,
        uint? localEntityId,
        out RuntimePendingPickup pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingPickup is not { } current
            || current.ServerGuid != serverGuid
            || (localEntityId is uint exact
                && current.LocalEntityId != exact))
        {
            pending = default;
            return false;
        }
        _pendingPickup = null;
        pending = current;
        IncrementRevision();
        return true;
    }

    public bool TryDispatchPickup(
        RuntimePendingPickup pickup,
        IRuntimeInteractionTransport transport,
        out uint sequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transport);
        sequence = 0u;
        if (!transport.IsInWorld)
            return false;

        uint sentSequence = 0u;
        bool dispatched = _inventory.TryDispatch(
            InventoryRequestKind.Pickup,
            pickup.ServerGuid,
            () => transport.TrySendPickup(
                pickup.ServerGuid,
                pickup.DestinationContainerId,
                pickup.Placement,
                out sentSequence),
            pickup.PendingPlacementToken);
        sequence = sentSequence;
        if (dispatched)
            IncrementRevision();
        return dispatched;
    }

    public bool TryArmPostArrivalUse(
        uint serverGuid,
        bool ownedByPlayer,
        bool useable,
        ItemUseRequestReservation? reservation,
        RuntimeInteractionApproachToken approachToken,
        out RuntimePendingUse pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (serverGuid == 0u
            || approachToken.ControllerLifetime == 0u
            || approachToken.ApproachGeneration == 0u)
        {
            pending = default;
            return false;
        }
        if (_pendingUse is not null)
        {
            pending = default;
            return false;
        }

        ulong token = ++_nextUseToken;
        if (token == 0u)
            token = ++_nextUseToken;
        pending = new RuntimePendingUse(
            token,
            serverGuid,
            ownedByPlayer,
            useable,
            reservation,
            approachToken);
        _pendingUse = pending;
        IncrementRevision();
        return true;
    }

    public bool TryResolveUseApproachCompletion(
        RuntimeInteractionApproachToken approachToken,
        bool natural,
        out RuntimePendingUse pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingUse is not { } current
            || current.ApproachToken != approachToken)
        {
            pending = default;
            return false;
        }

        _pendingUse = null;
        pending = current;
        IncrementRevision();
        return natural;
    }

    public bool TryGetPendingUse(out RuntimePendingUse pending)
    {
        if (_pendingUse is { } current)
        {
            pending = current;
            return true;
        }
        pending = default;
        return false;
    }

    public bool TryCancelPendingUse(out RuntimePendingUse pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingUse is not { } current)
        {
            pending = default;
            return false;
        }
        _pendingUse = null;
        pending = current;
        IncrementRevision();
        return true;
    }

    public bool TryCancelPendingUse(
        uint serverGuid,
        out RuntimePendingUse pending)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingUse is not { } current
            || current.ServerGuid != serverGuid)
        {
            pending = default;
            return false;
        }
        _pendingUse = null;
        pending = current;
        IncrementRevision();
        return true;
    }

    public void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResetCore(resetInventory: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        ResetCore(resetInventory: true);
        _disposed = true;
    }

    private void ResetCore(bool resetInventory)
    {
        bool changed =
            _awaitingAppraisalId != 0u
            || _currentAppraisalId != 0u
            || _outbound.Count != 0
            || _pendingPickup is not null
            || _pendingUse is not null
            || _lastUseSourceId != 0u
            || _lastUseTargetId != 0u
            || _awaitingItemUseCompletion
            || LastItemUseCompletion.Revision != 0
            || _lastUseMs != long.MinValue / 2;

        _pendingUse?.Reservation?.CancelBeforeDispatch();

        _lastUseSourceId = 0u;
        _lastUseTargetId = 0u;
        _awaitingItemUseCompletion = false;
        LastItemUseCompletion = default;
        _awaitingAppraisalId = 0u;
        _currentAppraisalId = 0u;
        _outbound.Clear();
        _pendingPickup = null;
        _pendingUse = null;
        _lastUseMs = long.MinValue / 2;
        _clearEpoch++;
        if (resetInventory)
            _inventory.ResetSession();
        if (changed)
            IncrementRevision();
    }

    private void IncrementRevision() =>
        Interlocked.Increment(ref _revision);
}

public interface IRuntimeInteractionTransport
{
    bool IsInWorld { get; }
    bool TrySendUse(uint serverGuid, out uint sequence);
    bool TrySendPickup(
        uint itemGuid,
        uint destinationContainerId,
        int placement,
        out uint sequence);
}
