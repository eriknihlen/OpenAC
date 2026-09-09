using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime;

public readonly record struct GameRuntimeEventOwnershipSnapshot(
    bool IsDisposeRequested,
    bool IsDisposed,
    bool OwnerEventsAttached,
    int ObserverCount,
    long DispatchFailureCount,
    bool HasLastDispatchFailure)
{
    public bool IsConverged =>
        IsDisposed
        && !OwnerEventsAttached
        && ObserverCount == 0;
}

internal interface IGameRuntimeEventSink
{
    void EmitCommand(
        RuntimeCommandDomain domain,
        int operation,
        RuntimeCommandStatus status,
        uint primaryObjectId = 0u,
        string? text = null);

    void EmitLifecycle(
        RuntimeLifecycleState previous,
        RuntimeLifecycleState current);

    void EmitMovement(in RuntimeMovementSnapshot movement);

    void EmitPortal(in RuntimePortalSnapshot portal);
}

internal sealed class GameRuntimeEventHub
    : IRuntimeEventSource,
      IGameRuntimeEventSink,
      IRuntimeEntityObjectObserver,
      IRuntimeCommunicationObserver,
      IDisposable
{
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly RuntimeCommunicationState _communication;
    private readonly RuntimeActionState _actions;
    private readonly object _observerGate = new();
    private IRuntimeEventObserver[] _observers = [];
    private IDisposable? _entityObjectSubscription;
    private IDisposable? _communicationSubscription;
    private bool _actionEventsAttached;
    private bool _disposeRequested;
    private bool _disposed;

    public GameRuntimeEventHub(
        RuntimeEntityObjectLifetime entityObjects,
        RuntimeCommunicationState communication,
        RuntimeActionState actions)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _communication = communication
            ?? throw new ArgumentNullException(nameof(communication));
        _actions = actions
            ?? throw new ArgumentNullException(nameof(actions));
    }

    public long DispatchFailureCount { get; private set; }
    public Exception? LastDispatchFailure { get; private set; }

    public GameRuntimeEventOwnershipSnapshot CaptureOwnership()
    {
        lock (_observerGate)
        {
            return new GameRuntimeEventOwnershipSnapshot(
                _disposeRequested,
                _disposed,
                OwnerEventsAttached,
                _observers.Length,
                DispatchFailureCount,
                LastDispatchFailure is not null);
        }
    }

    public IDisposable Subscribe(IRuntimeEventObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerGate)
        {
            ObjectDisposedException.ThrowIf(
                _disposeRequested || _disposed,
                this);
            IRuntimeEventObserver[] current = _observers;
            if (Array.IndexOf(current, observer) >= 0)
            {
                throw new InvalidOperationException(
                    "The runtime observer is already subscribed.");
            }

            if (!OwnerEventsAttached)
                AttachOwnerEvents();

            var replacement = new IRuntimeEventObserver[current.Length + 1];
            Array.Copy(current, replacement, current.Length);
            replacement[^1] = observer;
            Volatile.Write(ref _observers, replacement);
        }

        return new ObserverSubscription(this, observer);
    }

    public void Dispose()
    {
        lock (_observerGate)
        {
            if (_disposed)
                return;
            _disposeRequested = true;
            Volatile.Write(ref _observers, []);

            List<Exception>? failures = null;
            TryDispose(
                ref _communicationSubscription,
                "communication event subscription",
                ref failures);
            TryDispose(
                ref _entityObjectSubscription,
                "entity/object event subscription",
                ref failures);
            DetachActionEvents();
            _disposed = !OwnerEventsAttached;

            if (failures is not null)
            {
                throw new AggregateException(
                    "Runtime event subscriptions did not converge.",
                    failures);
            }
        }
    }

    public void EmitCommand(
        RuntimeCommandDomain domain,
        int operation,
        RuntimeCommandStatus status,
        uint primaryObjectId = 0u,
        string? text = null)
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        var delta = new RuntimeCommandDelta(
            NextStamp(),
            domain,
            operation,
            status,
            primaryObjectId,
            text);
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnCommand(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    public void EmitLifecycle(
        RuntimeLifecycleState previous,
        RuntimeLifecycleState current)
    {
        if (current == previous
            || !TryObservers(out IRuntimeEventObserver[] observers))
        {
            return;
        }

        var delta = new RuntimeLifecycleDelta(
            NextStamp(),
            previous,
            current);
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnLifecycle(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    public void EmitMovement(in RuntimeMovementSnapshot movement)
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        var delta = new RuntimeMovementDelta(NextStamp(), movement);
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnMovement(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    public void EmitPortal(in RuntimePortalSnapshot portal)
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        var delta = new RuntimePortalDelta(NextStamp(), portal);
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnPortal(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    public void OnEntity(in RuntimeEntityDelta delta)
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnEntity(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    public void OnInventory(in RuntimeInventoryDelta delta)
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnInventory(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    public void OnChat(in RuntimeCommunicationEvent committed)
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        var delta = new RuntimeChatDelta(NextStamp(), committed.Entry);
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnChat(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    private bool OwnerEventsAttached =>
        _entityObjectSubscription is not null
        || _communicationSubscription is not null
        || _actionEventsAttached;

    private void AttachOwnerEvents()
    {
        IDisposable? entity = null;
        IDisposable? communication = null;
        try
        {
            entity = _entityObjects.Events.Subscribe(this);
            communication = _communication.Events.Subscribe(this);
            _actions.CombatChanged += OnCombatChanged;
            _actionEventsAttached = true;
            _entityObjectSubscription = entity;
            _communicationSubscription = communication;
        }
        catch
        {
            DetachActionEvents();
            communication?.Dispose();
            entity?.Dispose();
            throw;
        }
    }

    private void Unsubscribe(IRuntimeEventObserver observer)
    {
        lock (_observerGate)
        {
            IRuntimeEventObserver[] current = _observers;
            int index = Array.IndexOf(current, observer);
            if (index < 0)
                return;
            if (current.Length == 1)
            {
                Volatile.Write(ref _observers, []);
                DetachOwnerEvents();
                return;
            }

            var replacement = new IRuntimeEventObserver[current.Length - 1];
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

    private void DetachOwnerEvents()
    {
        List<Exception>? failures = null;
        TryDispose(
            ref _communicationSubscription,
            "communication event subscription",
            ref failures);
        TryDispose(
            ref _entityObjectSubscription,
            "entity/object event subscription",
            ref failures);
        DetachActionEvents();
        if (failures is not null)
        {
            throw new AggregateException(
                "Runtime event subscriptions did not detach.",
                failures);
        }
    }

    private static void TryDispose(
        ref IDisposable? subscription,
        string name,
        ref List<Exception>? failures)
    {
        if (subscription is null)
            return;
        try
        {
            subscription.Dispose();
            subscription = null;
        }
        catch (Exception error)
        {
            (failures ??= []).Add(new InvalidOperationException(
                $"{name} did not detach.",
                error));
        }
    }

    private RuntimeEventStamp NextStamp() =>
        _entityObjects.Events.NextStamp();

    private void OnCombatChanged()
    {
        if (!TryObservers(out IRuntimeEventObserver[] observers))
            return;
        RuntimeActionSnapshot actions = _actions.View.Snapshot;
        var delta = new RuntimeCombatDelta(
            NextStamp(),
            actions.CombatMode,
            actions.TrackedTargetHealthCount,
            actions.CombatAttack);
        foreach (IRuntimeEventObserver observer in observers)
        {
            try
            {
                observer.OnCombat(in delta);
            }
            catch (Exception error)
            {
                RecordDispatchFailure(error);
            }
        }
    }

    private void DetachActionEvents()
    {
        if (!_actionEventsAttached)
            return;
        _actions.CombatChanged -= OnCombatChanged;
        _actionEventsAttached = false;
    }

    private bool TryObservers(out IRuntimeEventObserver[] observers)
    {
        observers = Volatile.Read(ref _observers);
        return observers.Length != 0;
    }

    private void RecordDispatchFailure(Exception error)
    {
        DispatchFailureCount++;
        LastDispatchFailure = error;
    }

    private sealed class ObserverSubscription(
        GameRuntimeEventHub owner,
        IRuntimeEventObserver observer)
        : IDisposable
    {
        private GameRuntimeEventHub? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(observer);
    }
}
