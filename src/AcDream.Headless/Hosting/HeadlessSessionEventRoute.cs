using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessSessionEventRoute : ILiveSessionEventRouting
{
    private readonly ILiveSessionEventRouting _events;
    private readonly GameRuntime _runtime;
    private readonly IRuntimePlacementProjectionSink _placements;
    private readonly RuntimeFirstEntryDriveController? _firstEntry;
    private readonly RuntimeAcceptedPositionDriveController? _acceptedPositionDrive;
    private readonly Action<RuntimeEntityRecord>? _localPlayerCompleted;
    private RuntimePlacementProjectionSubscription? _subscription;
    private RuntimeGenerationToken _attachedGeneration;
    private bool _attachStarted;
    private bool _eventsDisposed;
    private bool _disposed;

    internal HeadlessSessionEventRoute(
        ILiveSessionEventRouting events,
        GameRuntime runtime,
        IRuntimePlacementProjectionSink placements,
        RuntimeFirstEntryDriveController? firstEntry = null,
        Action<RuntimeEntityRecord>? localPlayerCompleted = null,
        RuntimeAcceptedPositionDriveController? acceptedPositionDrive = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _placements = placements
            ?? throw new ArgumentNullException(nameof(placements));
        _firstEntry = firstEntry;
        _localPlayerCompleted = localPlayerCompleted;
        _acceptedPositionDrive = acceptedPositionDrive;
    }

    public void Attach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_attachStarted)
            return;

        _attachStarted = true;
        _attachedGeneration = _runtime.Generation;
        _firstEntry?.AttachRoute(this, _localPlayerCompleted);
        _acceptedPositionDrive?.AttachRoute(this);
        _events.Attach();
        _subscription = new RuntimePlacementProjectionSubscription(
            _runtime,
            _placements);
    }

    internal bool RetryPending()
    {
        if (_subscription is null
            || _attachedGeneration != _runtime.Generation
            || !_subscription.HasPendingReceipts)
        {
            return false;
        }
        return _subscription.RetryPending();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Subscription disposal is idempotent and deliberately precedes the
        // network route. A still-pending FIFO head remains Runtime-owned for
        // the replacement route to drain.
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        _firstEntry?.DetachRoute(this);
        _acceptedPositionDrive?.DetachRoute(this);
        if (!_eventsDisposed)
        {
            _events.Dispose();
            _eventsDisposed = true;
        }

        _disposed = true;
    }
}
