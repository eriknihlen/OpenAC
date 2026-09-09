using AcDream.Core.Items;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Entities;

public interface IRuntimeEntityObjectObserver
{
    void OnEntity(in RuntimeEntityDelta delta);

    void OnInventory(in RuntimeInventoryDelta delta);
}

public interface IRuntimeEntityObjectEventSource
{
    IDisposable Subscribe(IRuntimeEntityObjectObserver observer);
}

public interface IRuntimePlacementObserver
{
    void OnPlacement(in RuntimePlacementDelta delta);
}

public sealed class RuntimeEntityObjectEventStream
    : IRuntimeEntityObjectEventSource,
      IDisposable
{
    private readonly RuntimeEntityDirectory _entities;
    private readonly ClientObjectTable _objects;
    private readonly RuntimeEventSequencer _sequencer = new();
    private readonly object _observerGate = new();
    private readonly List<PendingDispatch> _pendingDispatch = [];
    private IRuntimeEntityObjectObserver[] _observers = [];
    private IRuntimePlacementObserver[] _placementObservers = [];
    private Func<RuntimeGenerationToken> _generation = static () => default;
    private Func<ulong> _frameNumber = static () => 0UL;
    private bool _contextBound;
    private bool _dispatching;
    private bool _disposed;

    internal RuntimeEntityObjectEventStream(
        RuntimeEntityDirectory entities,
        ClientObjectTable objects)
    {
        _entities = entities
            ?? throw new ArgumentNullException(nameof(entities));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _objects.ObjectAdded += OnObjectAdded;
        _objects.ObjectUpdated += OnObjectUpdated;
        _objects.ObjectMoved += OnObjectMoved;
        _objects.ObjectRemovalClassified += OnObjectRemoved;
        _objects.Cleared += OnObjectsCleared;
    }

    public ulong LastSequence => _sequencer.LastSequence;
    public int SubscriberCount => Volatile.Read(ref _observers).Length;
    public int PlacementSubscriberCount =>
        Volatile.Read(ref _placementObservers).Length;
    public int PendingDispatchCount => _pendingDispatch.Count;
    public bool IsDispatching => _dispatching;
    public long DispatchFailureCount { get; private set; }
    public Exception? LastDispatchFailure { get; private set; }

    public void BindContext(
        Func<RuntimeGenerationToken> generation,
        Func<ulong> frameNumber)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(frameNumber);
        lock (_observerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_contextBound)
            {
                throw new InvalidOperationException(
                    "The Runtime entity/object event context is already bound.");
            }

            _generation = generation;
            _frameNumber = frameNumber;
            _contextBound = true;
        }
    }

    public IDisposable Subscribe(IRuntimeEntityObjectObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IRuntimeEntityObjectObserver[] current = _observers;
            if (Array.IndexOf(current, observer) >= 0)
            {
                throw new InvalidOperationException(
                    "The Runtime entity/object observer is already subscribed.");
            }

            var replacement =
                new IRuntimeEntityObjectObserver[current.Length + 1];
            Array.Copy(current, replacement, current.Length);
            replacement[^1] = observer;
            Volatile.Write(ref _observers, replacement);
        }

        return new ObserverSubscription(this, observer);
    }

    public IDisposable SubscribePlacement(IRuntimePlacementObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IRuntimePlacementObserver[] current = _placementObservers;
            if (Array.IndexOf(current, observer) >= 0)
            {
                throw new InvalidOperationException(
                    "The Runtime placement observer is already subscribed.");
            }
            var replacement = new IRuntimePlacementObserver[current.Length + 1];
            Array.Copy(current, replacement, current.Length);
            replacement[^1] = observer;
            Volatile.Write(ref _placementObservers, replacement);
        }
        return new PlacementObserverSubscription(this, observer);
    }

    /// <summary>
    /// Allocates the next stamp for another domain on the same Runtime event
    /// surface. J4–J6 will move those remaining domain publishers into
    /// Runtime; until then the App adapter borrows this sequencer.
    /// </summary>
    public RuntimeEventStamp NextStamp()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed),
            this);
        return _sequencer.Next(_generation(), _frameNumber());
    }

    internal void PublishEntity(
        RuntimeEntityChange change,
        RuntimeEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var delta = new RuntimeEntityDelta(
            NextStamp(),
            change,
            RuntimeEntityObjectViews.Snapshot(record));
        EnqueueAndDrain(PendingDispatch.ForEntity(delta));
    }

    internal void PublishPlacement(
        in RuntimePlacementProjectionSnapshot placement)
    {
        var delta = new RuntimePlacementDelta(
            NextStamp(),
            placement);
        EnqueueAndDrain(PendingDispatch.ForPlacement(delta));
    }

    public void Dispose()
    {
        lock (_observerGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Volatile.Write(ref _observers, []);
            Volatile.Write(ref _placementObservers, []);
            _objects.Cleared -= OnObjectsCleared;
            _objects.ObjectRemovalClassified -= OnObjectRemoved;
            _objects.ObjectMoved -= OnObjectMoved;
            _objects.ObjectUpdated -= OnObjectUpdated;
            _objects.ObjectAdded -= OnObjectAdded;
        }
    }

    internal void DetachObservers()
    {
        lock (_observerGate)
        {
            if (_disposed)
                return;
            Volatile.Write(ref _observers, []);
            Volatile.Write(ref _placementObservers, []);
        }
    }

    private void OnObjectAdded(ClientObject item) =>
        PublishInventory(RuntimeInventoryChange.Added, item);

    private void OnObjectUpdated(ClientObject item) =>
        PublishInventory(RuntimeInventoryChange.Updated, item);

    private void OnObjectMoved(ClientObjectMove move)
    {
        ClientObject? item = move.Item ?? _objects.Get(move.ItemId);
        RuntimeInventoryItemSnapshot snapshot = item is null
            ? new RuntimeInventoryItemSnapshot(
                move.ItemId,
                0,
                string.Empty,
                move.Current.ContainerId,
                move.Current.ContainerSlot,
                move.Current.WielderId,
                (uint)move.Current.EquipLocation,
                0,
                0)
            : RuntimeEntityObjectViews.Snapshot(item, _entities);
        PublishInventory(RuntimeInventoryChange.Moved, snapshot);
    }

    private void OnObjectRemoved(ClientObjectRemoval removal) =>
        PublishInventory(
            RuntimeInventoryChange.Removed,
            RuntimeEntityObjectViews.Snapshot(
                removal.Object,
                _entities,
                removal.Generation));

    private void OnObjectsCleared() =>
        PublishInventory(
            RuntimeInventoryChange.Cleared,
            default(RuntimeInventoryItemSnapshot));

    private void PublishInventory(
        RuntimeInventoryChange change,
        ClientObject item) =>
        PublishInventory(
            change,
            RuntimeEntityObjectViews.Snapshot(item, _entities));

    private void PublishInventory(
        RuntimeInventoryChange change,
        RuntimeInventoryItemSnapshot item)
    {
        var delta = new RuntimeInventoryDelta(
            NextStamp(),
            change,
            item);
        EnqueueAndDrain(PendingDispatch.ForInventory(delta));
    }

    private void EnqueueAndDrain(PendingDispatch pending)
    {
        _pendingDispatch.Add(pending);
        if (_dispatching)
            return;

        _dispatching = true;
        try
        {
            for (int index = 0; index < _pendingDispatch.Count; index++)
                Dispatch(_pendingDispatch[index]);
        }
        finally
        {
            _pendingDispatch.Clear();
            _dispatching = false;
        }
    }

    private void Dispatch(PendingDispatch pending)
    {
        IRuntimeEntityObjectObserver[] observers =
            Volatile.Read(ref _observers);
        foreach (IRuntimeEntityObjectObserver observer in observers)
        {
            try
            {
                if (pending.Kind is PendingDispatchKind.Entity)
                {
                    RuntimeEntityDelta delta = pending.Entity;
                    observer.OnEntity(in delta);
                }
                else if (pending.Kind is PendingDispatchKind.Inventory)
                {
                    RuntimeInventoryDelta delta = pending.Inventory;
                    observer.OnInventory(in delta);
                }
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }

        if (pending.Kind is PendingDispatchKind.Placement)
        {
            IRuntimePlacementObserver[] placementObservers =
                Volatile.Read(ref _placementObservers);
            foreach (IRuntimePlacementObserver observer in placementObservers)
            {
                try
                {
                    RuntimePlacementDelta delta = pending.Placement;
                    observer.OnPlacement(in delta);
                }
                catch (Exception error)
                {
                    RecordDispatchFailure(error);
                }
            }
        }
    }

    private void RecordDispatchFailure(Exception error)
    {
        DispatchFailureCount++;
        LastDispatchFailure = error;
    }

    private void Unsubscribe(IRuntimeEntityObjectObserver observer)
    {
        lock (_observerGate)
        {
            IRuntimeEntityObjectObserver[] current = _observers;
            int index = Array.IndexOf(current, observer);
            if (index < 0)
                return;
            if (current.Length == 1)
            {
                Volatile.Write(ref _observers, []);
                return;
            }

            var replacement =
                new IRuntimeEntityObjectObserver[current.Length - 1];
            if (index != 0)
                Array.Copy(current, 0, replacement, 0, index);
            if (index != current.Length - 1)
            {
                Array.Copy(
                    current,
                    index + 1,
                    replacement,
                    index,
                    current.Length - index - 1);
            }
            Volatile.Write(ref _observers, replacement);
        }
    }

    private void UnsubscribePlacement(IRuntimePlacementObserver observer)
    {
        lock (_observerGate)
        {
            IRuntimePlacementObserver[] current = _placementObservers;
            int index = Array.IndexOf(current, observer);
            if (index < 0)
                return;
            var replacement = new IRuntimePlacementObserver[current.Length - 1];
            if (index != 0)
                Array.Copy(current, 0, replacement, 0, index);
            if (index != current.Length - 1)
            {
                Array.Copy(
                    current,
                    index + 1,
                    replacement,
                    index,
                    current.Length - index - 1);
            }
            Volatile.Write(ref _placementObservers, replacement);
        }
    }

    private enum PendingDispatchKind : byte
    {
        Entity,
        Inventory,
        Placement,
    }

    private readonly record struct PendingDispatch(
        PendingDispatchKind Kind,
        RuntimeEntityDelta Entity,
        RuntimeInventoryDelta Inventory,
        RuntimePlacementDelta Placement)
    {
        public static PendingDispatch ForEntity(
            RuntimeEntityDelta entity) =>
            new(PendingDispatchKind.Entity, entity, default, default);

        public static PendingDispatch ForInventory(
            RuntimeInventoryDelta inventory) =>
            new(PendingDispatchKind.Inventory, default, inventory, default);

        public static PendingDispatch ForPlacement(
            RuntimePlacementDelta placement) =>
            new(PendingDispatchKind.Placement, default, default, placement);
    }

    private sealed class ObserverSubscription(
        RuntimeEntityObjectEventStream owner,
        IRuntimeEntityObjectObserver observer)
        : IDisposable
    {
        private RuntimeEntityObjectEventStream? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(observer);
    }


    private sealed class PlacementObserverSubscription(
        RuntimeEntityObjectEventStream owner,
        IRuntimePlacementObserver observer)
        : IDisposable
    {
        private RuntimeEntityObjectEventStream? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?
                .UnsubscribePlacement(observer);
    }
}
