using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

public interface IRuntimePlacementProjectionSink
{
    bool TryApply(in RuntimePlacementProjectionSnapshot projection);
}

public sealed class RuntimePlacementProjectionSubscription
    : IRuntimePlacementObserver,
      IDisposable
{
    private readonly RuntimePlacementProjectionChannel _channel;
    private readonly Func<RuntimeGenerationToken> _generation;
    private readonly IRuntimePlacementProjectionSink _sink;
    private IDisposable? _subscription;
    private RuntimePlacementProjectionToken _appliedAwaitingAcknowledgement;
    private bool _disposed;

    public RuntimePlacementProjectionSubscription(
        GameRuntime runtime,
        IRuntimePlacementProjectionSink sink)
        : this(runtime, sink, retryPendingOnSubscribe: true)
    {
    }

    public RuntimePlacementProjectionSubscription(
        GameRuntime runtime,
        IRuntimePlacementProjectionSink sink,
        bool retryPendingOnSubscribe)
        : this(
            runtime?.Placements
                ?? throw new ArgumentNullException(nameof(runtime)),
            () => runtime.Generation,
            sink,
            retryPendingOnSubscribe)
    {
    }

    internal RuntimePlacementProjectionSubscription(
        RuntimePlacementProjectionChannel channel,
        Func<RuntimeGenerationToken> generation,
        IRuntimePlacementProjectionSink sink)
        : this(
            channel,
            generation,
            sink,
            retryPendingOnSubscribe: true)
    {
    }

    internal RuntimePlacementProjectionSubscription(
        RuntimePlacementProjectionChannel channel,
        Func<RuntimeGenerationToken> generation,
        IRuntimePlacementProjectionSink sink,
        bool retryPendingOnSubscribe)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _generation = generation
            ?? throw new ArgumentNullException(nameof(generation));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _subscription = _channel.Subscribe(this);
        if (retryPendingOnSubscribe)
            _ = RetryPending();
    }

    public bool HasAppliedReceiptAwaitingAcknowledgement =>
        _appliedAwaitingAcknowledgement.IsValid;

    public bool HasPendingReceipts => _channel.PendingCount != 0;

    public bool RetryPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RuntimeGenerationToken generation = _generation();
        if (_appliedAwaitingAcknowledgement.IsValid
            && (!_channel.TryPeek(
                    generation,
                    out RuntimePlacementProjectionSnapshot head)
                || head.Token != _appliedAwaitingAcknowledgement))
        {
            _appliedAwaitingAcknowledgement = default;
        }
        return _channel.RetryPending(generation);
    }

    public void OnPlacement(in RuntimePlacementDelta delta)
    {
        if (_disposed
            || !_channel.TryPeek(
                delta.Stamp.Generation,
                out RuntimePlacementProjectionSnapshot head)
            || head != delta.Placement)
        {
            return;
        }

        RuntimePlacementProjectionToken token = head.Token;
        if (_appliedAwaitingAcknowledgement != token)
        {
            if (!_sink.TryApply(in head))
                return;
            // A sink can synchronously tear down its host while applying a
            // receipt. Leave that receipt pending for the replacement host;
            // disposal is never permission to acknowledge afterward.
            if (_disposed)
                return;
            _appliedAwaitingAcknowledgement = token;
        }

        if (_channel.Acknowledge(delta.Stamp.Generation, token)
            && _appliedAwaitingAcknowledgement == token)
        {
            _appliedAwaitingAcknowledgement = default;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        _appliedAwaitingAcknowledgement = default;
    }
}
