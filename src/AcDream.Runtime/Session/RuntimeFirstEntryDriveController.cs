using AcDream.Content;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Session;

internal sealed class RuntimeFirstEntryDriveController
{
    private const int MaxSynchronousStepsPerEntity = 16;

    private sealed class Pending
    {
        internal required RuntimeEntityRecord Record { get; init; }
        internal required RuntimeInitialCreateResidenceToken Token { get; init; }
        internal required bool IsLocalPlayer { get; init; }
    }

    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly IGameRuntimeClock _clock;
    private readonly IPreparedCollisionSource _collisionSource;
    private readonly Func<PlayerMovementConstructionOptions> _localOptions;
    private readonly Func<RuntimeEntityRecord,
        RuntimeLocalPlayerPhysicsActivationPreparation> _localActivation;
    private readonly Dictionary<RuntimeEntityKey, Pending> _pending = [];
    private readonly List<RuntimeEntityKey> _driveScratch = [];
    private bool _driving;
    private object? _routeOwner;
    private bool _handOverOpen;
    private Action<RuntimeEntityRecord>? _localPlayerCompleted;
    private readonly List<Action> _passObservers = [];

    internal RuntimeFirstEntryDriveController(
        RuntimeEntityObjectLifetime entityObjects,
        IGameRuntimeClock clock,
        IPreparedCollisionSource collisionSource,
        Func<PlayerMovementConstructionOptions> localOptions,
        Func<RuntimeEntityRecord,
            RuntimeLocalPlayerPhysicsActivationPreparation> localActivation)
    {
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _collisionSource = collisionSource
            ?? throw new ArgumentNullException(nameof(collisionSource));
        _localOptions = localOptions
            ?? throw new ArgumentNullException(nameof(localOptions));
        _localActivation = localActivation
            ?? throw new ArgumentNullException(nameof(localActivation));
        _entityObjects.BindInitialResidenceBeginNotification(
            NoteResidenceBegan);
        // C3c-R1 review F5: tracked-but-undriven entries fold into the
        // entity-object ownership snapshot instead of sitting outside every
        // ledger.
        _entityObjects.RegisterFirstEntryDriveOwnership(() => _pending.Count);
    }

    internal int PendingCount => _pending.Count;

    private void NoteResidenceBegan(RuntimeEntityRecord record)
    {
        if (record.Key is not { } key
            || !_entityObjects.TryGetInitialCreateResidence(
                record,
                out RuntimeInitialCreateResidenceLease lease))
        {
            return;
        }

        _pending[key] = new Pending
        {
            Record = record,
            Token = lease.Token,
            IsLocalPlayer = lease.Route.OperationKind
                is RuntimeSetPositionOperationKind.InitialLogin,
        };
    }

    internal void DriveAll()
    {
        if (_driving || _pending.Count == 0)
            return;
        _driving = true;
        try
        {
            _driveScratch.Clear();
            foreach (RuntimeEntityKey key in _pending.Keys)
                _driveScratch.Add(key);
            foreach (RuntimeEntityKey key in _driveScratch)
            {
                if (_pending.TryGetValue(key, out Pending? pending))
                    DriveOne(key, pending);
            }
        }
        finally
        {
            _driving = false;
        }

        // Every first entry is driven here, whichever route pumps it, so a
        // pass is the one place an entry can finish and register the objects
        // that waited for it to arrive.
        for (int index = 0; index < _passObservers.Count; index++)
            _passObservers[index]();
    }

    /// <summary>
    /// Calls <paramref name="observer"/> after every pass that drove a first
    /// entry, from whichever route drove it, until the returned lease is
    /// disposed. A pass may finish an entry and register the objects that
    /// were held back until it arrived.
    /// </summary>
    internal IDisposable ObservePasses(Action observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _passObservers.Add(observer);
        return new PassObservation(this, observer);
    }

    private sealed class PassObservation(
        RuntimeFirstEntryDriveController owner,
        Action observer) : IDisposable
    {
        private RuntimeFirstEntryDriveController? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?._passObservers.Remove(observer);
    }

    /// <summary>
    /// Opens a hand-over: the route that lets go next keeps its work for the
    /// route that attaches after it, instead of abandoning it. Used when a
    /// window opens or closes on a session that keeps playing; a session
    /// reset never hands over.
    /// </summary>
    internal void BeginRouteHandOver() => _handOverOpen = true;

    /// <summary>
    /// Closes the hand-over. Work no route took is abandoned, as a plain
    /// detach would have.
    /// </summary>
    internal void EndRouteHandOver()
    {
        _handOverOpen = false;
        if (_routeOwner is null)
            _pending.Clear();
    }

    internal void AttachRoute(
        object route,
        Action<RuntimeEntityRecord>? localPlayerCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (_routeOwner is not null && !ReferenceEquals(_routeOwner, route))
        {
            throw new InvalidOperationException(
                "A first-entry drive controller serves one session route at "
                + "a time; the prior route must be disposed (session reset "
                + "precedes a new route) before a replacement attaches.");
        }
        _routeOwner = route;
        _localPlayerCompleted = localPlayerCompleted;
    }

    internal void DetachRoute(object route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!ReferenceEquals(_routeOwner, route))
            return;
        _routeOwner = null;
        _localPlayerCompleted = null;
        if (_handOverOpen)
            return;
        _pending.Clear();
    }

    private void DriveOne(RuntimeEntityKey key, Pending pending)
    {
        for (int step = 0; step < MaxSynchronousStepsPerEntity; step++)
        {
            if (pending.Record.Key != key)
            {
                _pending.Remove(key);
                return;
            }

            bool terminal;
            bool awaitingContinuationPlacement;
            bool localPlayerCompleted = false;
            if (pending.IsLocalPlayer)
            {
                RuntimeLocalPlayerFirstEntryStatus status =
                    _entityObjects.LocalPlayerFirstEntry.Advance(
                        pending.Record,
                        pending.Token,
                        _localOptions(),
                        _localActivation(pending.Record),
                        _collisionSource,
                        _clock.SimulationTimeSeconds,
                        inputs: default,
                        out _);
                terminal = status
                    is RuntimeLocalPlayerFirstEntryStatus.Completed
                    or RuntimeLocalPlayerFirstEntryStatus.RejectedToken
                    or RuntimeLocalPlayerFirstEntryStatus.RejectedAuthority;
                localPlayerCompleted = status
                    is RuntimeLocalPlayerFirstEntryStatus.Completed;
                awaitingContinuationPlacement = status
                    is RuntimeLocalPlayerFirstEntryStatus
                        .AwaitingContinuationPlacement;
            }
            else
            {
                RuntimeRemoteFirstEntryStatus status =
                    _entityObjects.RemoteFirstEntry.Advance(
                        pending.Record,
                        pending.Token,
                        _collisionSource,
                        _clock.SimulationTimeSeconds,
                        inputs: default,
                        out _,
                        out _);
                terminal = status
                    is RuntimeRemoteFirstEntryStatus.Completed
                    or RuntimeRemoteFirstEntryStatus.RejectedToken
                    or RuntimeRemoteFirstEntryStatus.RejectedAuthority;
                awaitingContinuationPlacement = status
                    is RuntimeRemoteFirstEntryStatus
                        .AwaitingContinuationPlacement;
            }

            if (terminal)
            {
                _pending.Remove(key);
                if (localPlayerCompleted)
                    _localPlayerCompleted?.Invoke(pending.Record);
                return;
            }
            if (!awaitingContinuationPlacement)
            {
                return;
            }
            if (!TryCompleteContinuationPlacement(
                    key,
                    pending.Record,
                    resolveWorldOffsetFromRuntimeFrame:
                        !pending.IsLocalPlayer))
                return;
        }
    }

    private bool TryCompleteContinuationPlacement(
        RuntimeEntityKey key,
        RuntimeEntityRecord record,
        bool resolveWorldOffsetFromRuntimeFrame)
    {
        RuntimeSetPositionState setPosition =
            _entityObjects.Physics.SetPosition;

        bool acknowledgedSomething = false;
        while (setPosition.TryPeekProjection(
                out RuntimePlacementProjectionSnapshot head)
            && head.Token.Entity == key
            && head.Kind is RuntimePlacementProjectionKind.Place
                or RuntimePlacementProjectionKind.Withdraw)
        {
            if (!setPosition.AcknowledgeProjection(head.Token))
                break;
            acknowledgedSomething = true;
        }

        if (!_entityObjects.InitialCreateExecution
                .TryGetPendingContinuationPlacement(
                    key,
                    out RuntimeEntityPlacementToken placement))
        {
            return acknowledgedSomething;
        }
        if (!_entityObjects.InitialCreateExecution
                .TryGetPendingContinuationRoute(
                    key,
                    out RuntimeAuthoritativePositionRoute route))
        {
            return acknowledgedSomething;
        }

        RuntimeSetPositionMoverPreparationStatus status =
            setPosition.TryPrepareAndSubmitAuthoredPlacement(
                record,
                placement,
                route.OperationKind,
                route.SetPositionFlags,
                _collisionSource,
                _clock.SimulationTimeSeconds,
                out RuntimeSetPositionOutcome outcome,
                resolveWorldOffsetFromRuntimeFrame:
                    resolveWorldOffsetFromRuntimeFrame);
        if (status != RuntimeSetPositionMoverPreparationStatus.Prepared)
        {
            // RetrySetupUnavailable retries on a later pump; a rejected
            // preparation for an already-submitted-and-awaiting operation is
            // driven purely by the head acknowledgements above.
            return acknowledgedSomething;
        }

        switch (outcome.Status)
        {
            case RuntimeSetPositionStatus.CommittedHostAcknowledgementPending:
                _ = setPosition.AcknowledgeProjection(outcome.Projection);
                return true;
            case RuntimeSetPositionStatus.DeferredCell:
                while (setPosition.TryPeekProjection(
                        out RuntimePlacementProjectionSnapshot parked)
                    && parked.Token.Entity == key
                    && parked.Kind is RuntimePlacementProjectionKind.Withdraw)
                {
                    if (!setPosition.AcknowledgeProjection(parked.Token))
                        break;
                    acknowledgedSomething = true;
                }
                return acknowledgedSomething;
            default:
                return true;
        }
    }
}
