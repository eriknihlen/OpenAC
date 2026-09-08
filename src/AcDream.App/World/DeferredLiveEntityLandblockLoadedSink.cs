namespace AcDream.App.World;

internal interface ILiveEntityLandblockLoadedSink
{
    void OnLandblockLoaded(uint landblockId);
}

internal sealed class DeferredLiveEntityLandblockLoadedSink
    : ILiveEntityLandblockLoadedSink
{
    private ILiveEntityLandblockLoadedSink? _target;
    private bool _deactivated;

    public void OnLandblockLoaded(uint landblockId)
    {
        if (!_deactivated)
            _target?.OnLandblockLoaded(landblockId);
    }

    public IDisposable Bind(ILiveEntityLandblockLoadedSink target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_deactivated, this);
        if (_target is not null)
        {
            throw new InvalidOperationException(
                "Live-entity landblock hydration is already bound.");
        }

        _target = target;
        return new Binding(this, target);
    }

    public void Deactivate()
    {
        _deactivated = true;
        _target = null;
    }

    private void Unbind(ILiveEntityLandblockLoadedSink expected)
    {
        if (ReferenceEquals(_target, expected))
            _target = null;
    }

    private sealed class Binding : IDisposable
    {
        private DeferredLiveEntityLandblockLoadedSink? _owner;
        private readonly ILiveEntityLandblockLoadedSink _expected;

        public Binding(
            DeferredLiveEntityLandblockLoadedSink owner,
            ILiveEntityLandblockLoadedSink expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_expected);
    }
}
