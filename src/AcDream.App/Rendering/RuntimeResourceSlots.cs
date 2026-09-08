namespace AcDream.App.Rendering;

/// <summary>Retryable one-owner slot for a disposable runtime resource.</summary>
internal sealed class OwnedResourceSlot<T>
    where T : class, IDisposable
{
    private T? _resource;
    private bool _operationActive;

    public bool HasResource => _resource is not null;

    public T Acquire(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_operationActive || _resource is not null)
            throw new InvalidOperationException($"A {typeof(T).Name} is already owned.");

        _operationActive = true;
        try
        {
            T resource = factory()
                ?? throw new InvalidOperationException($"The {typeof(T).Name} factory returned null.");
            _resource = resource;
            return resource;
        }
        finally
        {
            _operationActive = false;
        }
    }

    public T Borrow()
    {
        if (_operationActive)
            throw new InvalidOperationException($"The {typeof(T).Name} owner is changing state.");
        return _resource
            ?? throw new InvalidOperationException($"No {typeof(T).Name} is currently owned.");
    }

    public void Release()
    {
        T? resource = _resource;
        if (resource is null)
            return;
        if (_operationActive)
            throw new InvalidOperationException($"The {typeof(T).Name} owner is changing state.");

        _operationActive = true;
        try
        {
            resource.Dispose();
            if (ReferenceEquals(_resource, resource))
                _resource = null;
        }
        finally
        {
            _operationActive = false;
        }
    }

    internal void TransferOwnership(T expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (_operationActive)
            throw new InvalidOperationException($"The {typeof(T).Name} owner is changing state.");
        if (!ReferenceEquals(_resource, expected))
            throw new InvalidOperationException(
                $"The expected {typeof(T).Name} is not owned by this slot.");
        _resource = null;
    }
}

internal sealed class TransferableResourceSlot<T>
    where T : class, IDisposable
{
    private readonly OwnedResourceSlot<T> _fallback = new();
    private bool _operationActive;
    private bool _prepared;

    public bool HasFallback => _fallback.HasResource;

    public T Acquire(Func<T> factory) => Execute(() =>
    {
        T resource = _fallback.Acquire(factory);
        _prepared = true;
        return resource;
    });

    public T AcquirePrepared(Func<T> factory, Action<T> prepare) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(prepare);

        T resource;
        if (_fallback.HasResource)
        {
            resource = _fallback.Borrow();
        }
        else
        {
            resource = _fallback.Acquire(factory);
            _prepared = false;
        }

        if (!_prepared)
        {
            prepare(resource);
            _prepared = true;
        }

        return resource;
    });

    public T Borrow()
    {
        if (_operationActive)
            throw new InvalidOperationException("The transferable resource owner is changing state.");
        return _fallback.Borrow();
    }

    public TOwner Transfer<TOwner>(Func<T, TOwner> destinationFactory)
        where TOwner : class
    {
        return Execute(() =>
        {
            ArgumentNullException.ThrowIfNull(destinationFactory);
            if (!_prepared)
                throw new InvalidOperationException(
                    "The transferable resource has not completed preparation.");
            T resource = _fallback.Borrow();
            TOwner owner = destinationFactory(resource)
                ?? throw new InvalidOperationException("The destination owner factory returned null.");

            _fallback.TransferOwnership(resource);
            _prepared = false;
            return owner;
        });
    }

    public void ReleaseFallback() => Execute(
        () =>
        {
            _fallback.Release();
            if (!_fallback.HasResource)
                _prepared = false;
            return true;
        });

    private TResult Execute<TResult>(Func<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_operationActive)
            throw new InvalidOperationException("The transferable resource owner is changing state.");

        _operationActive = true;
        try
        {
            return operation();
        }
        finally
        {
            _operationActive = false;
        }
    }
}
