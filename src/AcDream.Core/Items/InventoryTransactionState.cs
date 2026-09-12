namespace AcDream.Core.Items;

public enum InventoryRequestKind
{
    Pickup,
    PutInContainer,
    SplitToContainer,
    Merge,
    Move,
    DropToWorld,
    SplitToWorld,
    Wield,
    Give,
}

public readonly record struct PendingInventoryRequest(
    ulong Token,
    InventoryRequestKind Kind,
    uint ItemId,
    ClientObject? ItemIdentity,
    bool Dispatched);

public sealed class ItemUseRequestReservation
{
    private readonly Action<bool> _resolve;
    private int _resolved;

    internal ItemUseRequestReservation(Action<bool> resolve)
        => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    public void MarkDispatched()
    {
        if (Interlocked.Exchange(ref _resolved, 1) == 0)
            _resolve(true);
    }

    public void CancelBeforeDispatch()
    {
        if (Interlocked.Exchange(ref _resolved, 1) == 0)
            _resolve(false);
    }
}

public sealed class InventoryTransactionState : IDisposable
{
    private readonly ClientObjectTable _objects;
    private ulong _nextRequestToken;
    private ulong _useReservationGeneration;
    private PendingInventoryRequest? _pendingRequest;
    private int _busyCount;
    private long _dispatchFailureCount;
    private bool _disposed;

    public InventoryTransactionState(ClientObjectTable objects)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _objects.ObjectMoved += OnObjectMoved;
        _objects.MoveRequestFailed += OnMoveFailed;
        _objects.ObjectRemoved += OnObjectRemoved;
        _objects.StackSizeUpdated += OnStackSizeUpdated;
        _objects.Cleared += OnObjectsCleared;
    }

    public event Action? StateChanged;
    public event Action<PendingInventoryRequest>? RequestCompleted;

    public event Action<PendingInventoryRequest, uint>? RequestFailed;

    public event Action? ObjectTableCleared;

    public ClientObjectTable Objects => _objects;
    public int BusyCount => _busyCount;
    public bool HasPendingRequest => _pendingRequest is not null;
    public bool CanBeginRequest => _busyCount == 0 && _pendingRequest is null;
    public bool IsDisposed => _disposed;
    public long DispatchFailureCount =>
        Interlocked.Read(ref _dispatchFailureCount);
    public Exception? LastDispatchFailure { get; private set; }

    public bool TryReserve(
        InventoryRequestKind kind,
        uint itemId,
        out PendingInventoryRequest pending,
        Action<PendingInventoryRequest>? beforeStateChanged = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (itemId == 0u || !CanBeginRequest)
        {
            pending = default;
            return false;
        }

        ulong token = NextToken();
        pending = new PendingInventoryRequest(
            token,
            kind,
            itemId,
            _objects.Get(itemId),
            Dispatched: false);
        _pendingRequest = pending;

        if (beforeStateChanged is not null)
        {
            try
            {
                beforeStateChanged(pending);
            }
            catch
            {
                if (IsCurrent(pending))
                    _pendingRequest = null;
                throw;
            }
        }

        if (!IsCurrent(pending))
            return false;

        DispatchStateChanged();
        return IsCurrent(pending);
    }

    public bool TryDispatch(
        InventoryRequestKind kind,
        uint itemId,
        Func<bool> dispatch,
        ulong reservationToken = 0u)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (itemId == 0u)
            return false;

        PendingInventoryRequest reserved;
        if (reservationToken != 0u)
        {
            if (_pendingRequest is not { } current
                || current.Token != reservationToken
                || current.ItemId != itemId
                || current.Kind != kind
                || current.Dispatched)
            {
                return false;
            }
            reserved = current;
        }
        else if (!TryReserve(kind, itemId, out reserved))
        {
            return false;
        }

        bool dispatched;
        try
        {
            dispatched = dispatch();
        }
        catch
        {
            ClearPending(reserved.Token);
            throw;
        }

        if (!dispatched)
        {
            ClearPending(reserved.Token);
            return false;
        }

        if (_pendingRequest is { } currentRequest
            && currentRequest.Token == reserved.Token)
        {
            _pendingRequest = reserved with { Dispatched = true };
            DispatchStateChanged();
        }
        return true;
    }

    public bool TryGetPending(out PendingInventoryRequest pending)
    {
        if (_pendingRequest is { } current)
        {
            pending = current;
            return true;
        }
        pending = default;
        return false;
    }

    public bool CancelBeforeDispatch(ulong token)
    {
        if (_pendingRequest is not { } pending
            || pending.Token != token
            || pending.Dispatched)
        {
            return false;
        }
        _pendingRequest = null;
        DispatchStateChanged();
        return true;
    }

    public void IncrementBusyCount()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _busyCount++;
        DispatchStateChanged();
    }

    public ItemUseRequestReservation BeginUseRequestReservation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong generation = _useReservationGeneration;
        _busyCount++;
        DispatchStateChanged();
        return new ItemUseRequestReservation(dispatched =>
        {
            if (generation != _useReservationGeneration || dispatched)
                return;
            if (_busyCount > 0)
                _busyCount--;
            DispatchStateChanged();
        });
    }

    public void CompleteUse(uint _)
    {
        if (_busyCount == 0)
            return;
        _busyCount--;
        DispatchStateChanged();
    }

    public void ClearBusy()
    {
        if (_busyCount == 0)
            return;
        _useReservationGeneration++;
        _busyCount = 0;
        DispatchStateChanged();
    }

    public void ResetSession()
    {
        bool changed = _pendingRequest is not null || _busyCount != 0;
        _pendingRequest = null;
        _useReservationGeneration++;
        _busyCount = 0;
        if (changed)
            DispatchStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _objects.Cleared -= OnObjectsCleared;
        _objects.StackSizeUpdated -= OnStackSizeUpdated;
        _objects.ObjectRemoved -= OnObjectRemoved;
        _objects.MoveRequestFailed -= OnMoveFailed;
        _objects.ObjectMoved -= OnObjectMoved;
        _pendingRequest = null;
        _useReservationGeneration++;
        _busyCount = 0;
        StateChanged = null;
        RequestCompleted = null;
        RequestFailed = null;
        ObjectTableCleared = null;
    }

    private ulong NextToken()
    {
        ulong token = ++_nextRequestToken;
        if (token == 0u)
            token = ++_nextRequestToken;
        return token;
    }

    private bool IsCurrent(PendingInventoryRequest expected) =>
        _pendingRequest is { } current
        && current.Token == expected.Token
        && current.ItemId == expected.ItemId
        && current.Kind == expected.Kind
        && !current.Dispatched;

    private void ClearPending(ulong token)
    {
        if (_pendingRequest is not { } pending || pending.Token != token)
            return;
        _pendingRequest = null;
        DispatchStateChanged();
    }

    private void OnObjectMoved(ClientObjectMove move)
    {
        if (move.Origin == ClientObjectMoveOrigin.AuthoritativeResponse)
            CompleteInventoryResponse(move.ItemId, move.Item);
    }

    private void OnMoveFailed(MoveRequestFailure failure)
    {
        uint itemId = failure.ItemId;
        // While a request is pending, a move failure is about that request
        // whatever guid the wire carries (the server may send none, or the
        // guid of a merge target); the wire guid only matters when nothing
        // is pending.
        if (_pendingRequest is { } current)
            itemId = current.ItemId;
        if (CompleteInventoryResponse(itemId, _objects.Get(itemId)) is { } failed)
            Dispatch(RequestFailed, failed, failure.WeenieError);
    }

    private void OnObjectRemoved(ClientObject item) =>
        CompleteInventoryResponse(item.ObjectId, item);

    private void OnStackSizeUpdated(ClientObject item) =>
        CompleteInventoryResponse(item.ObjectId, item);

    private PendingInventoryRequest? CompleteInventoryResponse(
        uint itemId,
        ClientObject? identity)
    {
        if (_pendingRequest is not { } request
            || request.ItemId != itemId
            || !MatchesIdentity(request.ItemIdentity, itemId, identity))
        {
            return null;
        }

        _pendingRequest = null;
        Dispatch(RequestCompleted, request);
        DispatchStateChanged();
        return request;
    }

    private bool MatchesIdentity(
        ClientObject? expected,
        uint itemId,
        ClientObject? actual)
    {
        if (expected is null)
            return true;
        if (actual is not null)
            return ReferenceEquals(expected, actual);
        return _objects.Get(itemId) is not { } current
            || ReferenceEquals(expected, current);
    }

    private void OnObjectsCleared()
    {
        bool changed = _pendingRequest is not null;
        _pendingRequest = null;
        Dispatch(ObjectTableCleared);
        if (changed)
            DispatchStateChanged();
    }

    private void DispatchStateChanged() => Dispatch(StateChanged);

    private void Dispatch(Action? listeners)
    {
        if (listeners is null)
            return;
        foreach (Action listener in listeners.GetInvocationList())
        {
            try
            {
                listener();
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    private void Dispatch<T>(Action<T>? listeners, T value)
    {
        if (listeners is null)
            return;
        foreach (Action<T> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(value);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    private void Dispatch<T1, T2>(Action<T1, T2>? listeners, T1 first, T2 second)
    {
        if (listeners is null)
            return;
        foreach (Action<T1, T2> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(first, second);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    private void RecordDispatchFailure(Exception error)
    {
        Interlocked.Increment(ref _dispatchFailureCount);
        LastDispatchFailure = error;
    }
}
