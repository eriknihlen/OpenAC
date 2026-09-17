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

/// <summary>
/// Who asked for an appraisal: a deliberate user action (the assess
/// keybind/click, or a headless bot's equivalent "examine selected"
/// command) versus a plugin polling object state in the background
/// (tracker/loot-scanner style Identify calls). The examination window
/// only opens or retargets for User-originated requests -- see
/// AcceptAppraisalResponse and AppraisalUiController.Apply.
/// </summary>
public enum AppraisalRequestOrigin
{
    User = 0,
    Automation = 1,
}

/// <summary>
/// The result of AcceptAppraisalResponse. Origin and PresentInUi are only
/// meaningful when FirstResponse is true (the response won the awaiting
/// slot); for a RefreshCurrentAppraisal re-request (FirstResponse false)
/// Origin reports User because that call is always made on the window's
/// own behalf, and PresentInUi is always true because it can only ever
/// land on the object already current. When Accepted is false every other
/// field is meaningless -- the response matched neither the awaiting nor
/// the current object and nothing changed.
/// </summary>
public readonly record struct RuntimeAppraisalResponseAcceptance(
    bool Accepted,
    bool FirstResponse,
    AppraisalRequestOrigin Origin,
    bool PresentInUi);

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
    private AppraisalRequestOrigin _awaitingAppraisalOrigin;
    private uint _currentAppraisalId;
    private uint _lastCompletedAppraisalId;
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
    public AppraisalRequestOrigin AwaitingAppraisalOrigin => _awaitingAppraisalOrigin;

    /// <summary>
    /// The presentation target: the object the examination window shows
    /// (or would show once it opens). Only a User-originated response
    /// retargets this, plus an Automation response that happens to land on
    /// the object already current -- see AcceptAppraisalResponse. Plugin
    /// completion tracking (ILootAutomation.Appraisal.CurrentObjectId, via
    /// RuntimeAutomationSurface) must NOT read this -- use
    /// LastCompletedAppraisalId instead, which advances for every origin.
    /// </summary>
    public uint CurrentAppraisalId => _currentAppraisalId;

    /// <summary>
    /// The completion signal: the object id of the most recent appraisal
    /// response that won the awaiting slot, regardless of origin or
    /// whether it retargeted the presentation window. Plugins (loot
    /// scanners, trackers) poll this -- via
    /// ILootAutomation.Appraisal.CurrentObjectId -- to learn that their own
    /// Identify request completed, which must not depend on whether the
    /// user's examination window happens to be showing that object. It is
    /// a one-shot signal for the request that produced it, not a history:
    /// TryRequestAppraisal clears it the moment a new request for THAT
    /// SAME object is accepted, and CancelObjectAppraisalForSpell clears
    /// it unconditionally along with the rest of the slot. A poll of
    /// "CurrentObjectId == myId" is only meaningful for the request you
    /// yourself most recently issued for myId -- an earlier completion of
    /// the same id does not linger to be misread as this one's.
    /// </summary>
    public uint LastCompletedAppraisalId => _lastCompletedAppraisalId;

    /// <summary>
    /// Raised for every accepted appraisal response for an object,
    /// including a RefreshCurrentAppraisal re-request that lands on an
    /// object already current -- its data can still have changed since the
    /// first reveal. Use AcceptAppraisalResponse's FirstResponse result to
    /// distinguish the initial reveal from a refresh if that matters to the
    /// caller; this event by itself does not.
    /// </summary>
    public event Action<uint>? AppraisalReceived;

    /// <summary>
    /// Raised for every completed "use", regardless of whether this state
    /// was the one awaiting it (LastItemUseCompletion only advances for a
    /// use this state itself dispatched via TryDispatchUse /
    /// TryDispatchTargetedUse). A caller that starts a use with its own
    /// reservation -- BeginUseRequestReservation() without going through
    /// TryDispatchUse -- correlates the outcome itself against this event,
    /// the same generic completion the retail-look window observes.
    /// </summary>
    public event Action<uint>? UseCompleted;

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
        try { UseCompleted?.Invoke(error); }
        catch { /* observer errors do not interrupt use-completion bookkeeping */ }
    }

    /// <summary>
    /// Asks the server to appraise an object. A quiet appraisal, such as one a walk asks
    /// for to learn whether a door is locked, updates the object without becoming the
    /// appraisal the character is looking at.
    /// </summary>
    public bool TryRequestAppraisal(
        uint objectId,
        Action<uint> sendAppraisal,
        AppraisalRequestOrigin origin = AppraisalRequestOrigin.User)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sendAppraisal);
        if (objectId == 0u)
            return false;

        // A background plugin Identify must never bump a deliberate user
        // assess out of the single awaiting slot -- that would make the
        // user's own assess action produce nothing. The caller's ordinary
        // CanBeginRequest/busy gate already prevents this in practice (the
        // user's request holds the one busy reference), so this only ever
        // bites a narrow same-tick race; it reports the same false a
        // transport failure would, which callers already treat as refused/
        // retry-next-scan rather than a hard error.
        if (origin == AppraisalRequestOrigin.Automation
            && _awaitingAppraisalId != 0u
            && _awaitingAppraisalOrigin == AppraisalRequestOrigin.User)
        {
            return false;
        }

        // LastCompletedAppraisalId is a one-shot completion signal, not a
        // history of every object ever appraised: a plugin re-identifying
        // the same object it already saw complete needs a fresh signal for
        // THIS request, not a stale true left over from the last one. Left
        // sticky, a plugin polling "CurrentObjectId == myId &&
        // AwaitingObjectId != myId" would see a false completion the
        // instant it issued the new request (both halves already true from
        // the prior round), or after this request was cancelled/displaced
        // without ever completing.
        if (objectId == _lastCompletedAppraisalId)
            _lastCompletedAppraisalId = 0u;

        uint epoch = _clearEpoch;
        bool acquiredBusy = _awaitingAppraisalId == 0u;
        if (acquiredBusy)
        {
            _inventory.IncrementBusyCount();
            if (_disposed || epoch != _clearEpoch)
                return false;
        }

        uint previousAwaiting = _awaitingAppraisalId;
        AppraisalRequestOrigin previousOrigin = _awaitingAppraisalOrigin;
        _awaitingAppraisalId = objectId;
        _awaitingAppraisalOrigin = origin;
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
                _awaitingAppraisalOrigin = previousOrigin;
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

        // A response landing on _awaitingAppraisalId is the request this
        // slot was waiting on -- a genuine completion, of either origin.
        // A response landing on _currentAppraisalId instead (the earlier
        // "objectId != _awaitingAppraisalId" branch of the guard above)
        // matches the current object but not the awaiting one: it is a
        // background refresh of what the window already shows, arriving
        // only through RefreshCurrentAppraisal, which is always made on
        // the window's own behalf.
        bool firstResponse = objectId == _awaitingAppraisalId;
        AppraisalRequestOrigin origin = firstResponse
            ? _awaitingAppraisalOrigin
            : AppraisalRequestOrigin.User;

        // CurrentAppraisalId is presentation, not completion: a
        // plugin-originated (Automation) response never retargets the
        // examination window away from whatever object it already shows --
        // it only ever "wins" the current slot when its response happens to
        // land on the object that is already current (a silent background
        // re-identify of the item the user is looking at, which should
        // still refresh that window's content in place). A
        // User-originated response always retargets. See
        // AppraisalUiController.Apply for the presentation side of this
        // rule. LastCompletedAppraisalId is the separate completion signal
        // plugins poll (ILootAutomation.Appraisal.CurrentObjectId) -- it
        // advances for every completed response below regardless of
        // origin or retargeting, because a plugin's own Identify
        // completing must not depend on what the user's window shows.
        bool retargetsCurrent =
            firstResponse
            && (origin == AppraisalRequestOrigin.User
                || objectId == _currentAppraisalId);
        bool presentInUi = !firstResponse || retargetsCurrent;

        if (firstResponse)
        {
            _awaitingAppraisalId = 0u;
            _awaitingAppraisalOrigin = default;
            _lastCompletedAppraisalId = objectId;
            if (retargetsCurrent)
                _currentAppraisalId = objectId;
            _inventory.CompleteUse(0u);
            IncrementRevision();
        }

        // Raised for every accepted response, not just the first: a
        // RefreshCurrentAppraisal re-request lands here with
        // firstResponse == false because the object is already current, but
        // its payload can still have changed (durability ticked, a stack
        // count moved) and observers need the update. Consumers that only
        // care about the initial reveal can dedupe on FirstResponse
        // themselves; this event alone cannot tell them which response an
        // invocation carries, so IEvents.ObjectChanged is not affected. It
        // also fires for a plugin-originated appraisal that never touches
        // the examination window -- plugin object-property observers need
        // the data regardless of what the UI does with it.
        try { AppraisalReceived?.Invoke(objectId); }
        catch { /* observer errors do not interrupt appraisal bookkeeping */ }

        return new RuntimeAppraisalResponseAcceptance(
            Accepted: true,
            FirstResponse: firstResponse,
            Origin: origin,
            PresentInUi: presentInUi);
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
        _awaitingAppraisalOrigin = default;
        _currentAppraisalId = 0u;
        // Wipe the completion signal along with the rest of the slot --
        // this is a full appraisal-state takeover for the spell-examine
        // view, and leaving a stale LastCompletedAppraisalId behind risks
        // a plugin later reading a "completion" that predates this cancel.
        _lastCompletedAppraisalId = 0u;
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
            || _lastCompletedAppraisalId != 0u
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
        _awaitingAppraisalOrigin = default;
        _currentAppraisalId = 0u;
        _lastCompletedAppraisalId = 0u;
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
