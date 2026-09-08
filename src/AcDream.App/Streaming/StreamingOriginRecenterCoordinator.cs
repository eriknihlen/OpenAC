using AcDream.App.World;

namespace AcDream.App.Streaming;

internal sealed class StreamingOriginRecenterCoordinator : IStreamingOriginConvergence
{
    private readonly record struct Request(
        int DestinationX,
        int DestinationY,
        bool IsSealedDungeon,
        bool CancelAtSessionBoundary = false);

    private readonly StreamingController _streaming;
    private readonly LiveWorldOriginState _origin;
    private Request? _pending;
    private Request? _replacement;
    private bool _originCommitted;
    private bool _acceptReplacement;
    private bool _sourceWasSealedDungeon;
    private bool _advancing;

    public StreamingOriginRecenterCoordinator(
        StreamingController streaming,
        LiveWorldOriginState origin)
    {
        _streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
    }

    public bool IsPending => _pending is not null;

    public bool Begin(int destinationX, int destinationY, bool isSealedDungeon)
    {
        var request = new Request(destinationX, destinationY, isSealedDungeon);
        if (_pending is { } pending && pending != request)
        {
            if (!_acceptReplacement)
            {
                throw new InvalidOperationException(
                    "A different streaming-origin recenter is already pending.");
            }

            _acceptReplacement = false;
            if (_originCommitted)
                _replacement = request;
            else
                _pending = request;
        }

        if (_pending is null)
        {
            _pending = request;
            _acceptReplacement = false;
            _sourceWasSealedDungeon = _streaming.IsCollapsedToDungeon;
            _streaming.BeginOriginRecenter();
        }

        return Advance();
    }

    public bool Advance()
    {
        if (_advancing)
            return false;

        _advancing = true;
        try
        {
            while (_pending is { } request)
            {
                if (!_originCommitted)
                {
                    if (!_streaming.IsOriginRecenterRetirementComplete())
                        return false;

                    if (_pending is not { } current || current != request)
                        continue;

                    if (!request.CancelAtSessionBoundary)
                        _origin.Recenter(request.DestinationX, request.DestinationY);
                    _originCommitted = true;
                }

                bool destinationCommitted = request.CancelAtSessionBoundary
                    ? _streaming.TryCancelOriginRecenter()
                    : _streaming.TryCommitOriginRecenter(
                        request.DestinationX,
                        request.DestinationY,
                        request.IsSealedDungeon);
                if (!destinationCommitted)
                    return false;

                if (_pending is not { } committed || committed != request)
                {
                    _originCommitted = false;
                    _streaming.BeginOriginRecenter();
                    continue;
                }

                _pending = null;
                _originCommitted = false;
                _acceptReplacement = false;

                if (_replacement is not { } replacement)
                    return true;

                _replacement = null;
                _pending = replacement;
                _sourceWasSealedDungeon = _streaming.IsCollapsedToDungeon;
                _streaming.BeginOriginRecenter();
            }

            return true;
        }
        finally
        {
            _advancing = false;
        }
    }

    public bool Reset(bool sessionEnding = false)
    {
        if (_pending is null)
        {
            if (!sessionEnding)
                return true;

            _pending = new Request(
                _origin.CenterX,
                _origin.CenterY,
                IsSealedDungeon: false,
                CancelAtSessionBoundary: true);
            _replacement = null;
            _originCommitted = false;
            _acceptReplacement = false;
            _sourceWasSealedDungeon = _streaming.IsCollapsedToDungeon;
            _streaming.BeginOriginRecenter();
            return Advance();
        }

        _replacement = null;
        _acceptReplacement = true;
        if (!_originCommitted)
        {
            _pending = new Request(
                _origin.CenterX,
                _origin.CenterY,
                IsSealedDungeon: !sessionEnding && _sourceWasSealedDungeon,
                CancelAtSessionBoundary: sessionEnding);
        }
        else if (sessionEnding)
        {
            _pending = new Request(
                _origin.CenterX,
                _origin.CenterY,
                IsSealedDungeon: false,
                CancelAtSessionBoundary: true);
        }

        return Advance();
    }
}
