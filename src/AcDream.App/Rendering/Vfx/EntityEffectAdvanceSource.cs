namespace AcDream.App.Rendering.Vfx;

internal interface IEntityEffectAdvanceSource
{
    bool CanAdvanceOwner(uint ownerLocalId);
}

internal sealed class DeferredEntityEffectAdvanceSource : IEntityEffectAdvanceSource
{
    private readonly object _gate = new();
    private IEntityEffectAdvanceSource? _target;
    private bool _deactivated;

    public void Bind(IEntityEffectAdvanceSource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_deactivated, this);
            if (_target is not null && !ReferenceEquals(_target, target))
            {
                throw new InvalidOperationException(
                    "Entity-effect script advancement is already bound.");
            }

            _target = target;
        }
    }

    public IDisposable BindOwned(IEntityEffectAdvanceSource target)
    {
        Bind(target);
        return new Binding(this, target);
    }

    public void Unbind(IEntityEffectAdvanceSource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (ReferenceEquals(_target, target))
                _target = null;
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            _deactivated = true;
            _target = null;
        }
    }

    public bool CanAdvanceOwner(uint ownerLocalId)
    {
        lock (_gate)
        {
            return _deactivated
                || _target?.CanAdvanceOwner(ownerLocalId) != false;
        }
    }

    private sealed class Binding : IDisposable
    {
        private DeferredEntityEffectAdvanceSource? _owner;
        private readonly IEntityEffectAdvanceSource _target;

        public Binding(
            DeferredEntityEffectAdvanceSource owner,
            IEntityEffectAdvanceSource target)
        {
            _owner = owner;
            _target = target;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unbind(_target);
    }
}
