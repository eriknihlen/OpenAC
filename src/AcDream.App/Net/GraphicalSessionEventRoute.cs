using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.App.Net;

internal sealed class GraphicalSessionEventRoute : ILiveSessionEventRouting
{
    private readonly ILiveSessionEventRouting _events;
    private readonly Func<RuntimePlacementProjectionSubscription>
        _createSubscription;
    private readonly Func<RuntimeGenerationToken> _generation;
    private readonly RuntimePlacementProjectionRetrySlot _retries;
    private readonly RuntimeFirstEntryDriveController? _firstEntry;
    private readonly RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private readonly RuntimeRemotePlacementDriveController? _remotePlacementDrive;
    private readonly Action<RuntimeEntityRecord>? _localPlayerCompleted;
    private RuntimePlacementProjectionSubscription? _subscription;
    private IDisposable? _retryLease;
    private bool _attachStarted;
    private bool _eventsDisposed;
    private bool _disposed;

    internal GraphicalSessionEventRoute(
        ILiveSessionEventRouting events,
        GameRuntime runtime,
        IRuntimePlacementProjectionSink placements,
        RuntimePlacementProjectionRetrySlot retries,
        RuntimeFirstEntryDriveController? firstEntry = null,
        Action<RuntimeEntityRecord>? localPlayerCompleted = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null,
        RuntimeRemotePlacementDriveController? remotePlacementDrive = null)
        : this(
            events,
            () => new RuntimePlacementProjectionSubscription(
                runtime,
                placements,
                retryPendingOnSubscribe: false),
            () => runtime.Generation,
            retries,
            firstEntry,
            localPlayerCompleted,
            acceptedPositionDrive,
            remotePlacementDrive)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(placements);
    }

    internal GraphicalSessionEventRoute(
        ILiveSessionEventRouting events,
        Func<RuntimePlacementProjectionSubscription> createSubscription,
        Func<RuntimeGenerationToken> generation,
        RuntimePlacementProjectionRetrySlot retries,
        RuntimeFirstEntryDriveController? firstEntry = null,
        Action<RuntimeEntityRecord>? localPlayerCompleted = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null,
        RuntimeRemotePlacementDriveController? remotePlacementDrive = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _createSubscription = createSubscription
            ?? throw new ArgumentNullException(nameof(createSubscription));
        _generation = generation
            ?? throw new ArgumentNullException(nameof(generation));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
        _firstEntry = firstEntry;
        _localPlayerCompleted = localPlayerCompleted;
        _acceptedPositionDrive = acceptedPositionDrive;
        _remotePlacementDrive = remotePlacementDrive;
    }

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attachStarted)
            return;

        _attachStarted = true;
        _firstEntry?.AttachRoute(this, _localPlayerCompleted);
        _acceptedPositionDrive?.AttachRoute(this);
        _remotePlacementDrive?.AttachRoute(this);
        _events.Attach();

        RuntimePlacementProjectionSubscription? subscription = null;
        IDisposable? retryLease = null;
        try
        {
            subscription = _createSubscription();
            RuntimePlacementProjectionSubscription boundSubscription =
                subscription;
            retryLease = _retries.BindOwned(
                _generation(),
                () =>
                {
                    _firstEntry?.DriveAll();
                    _acceptedPositionDrive?.Advance();
                    _remotePlacementDrive?.Advance();
                    return boundSubscription.RetryPending();
                });
            _subscription = subscription;
            _retryLease = retryLease;
            _ = subscription.RetryPending();
        }
        catch
        {
            retryLease?.Dispose();
            subscription?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Interlocked.Exchange(ref _retryLease, null)?.Dispose();
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        _firstEntry?.DetachRoute(this);
        _acceptedPositionDrive?.DetachRoute(this);
        _remotePlacementDrive?.DetachRoute(this);
        if (!_eventsDisposed)
        {
            _events.Dispose();
            _eventsDisposed = true;
        }

        _disposed = true;
    }
}
