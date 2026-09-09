using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Plugins;

internal sealed class ScopedRenderPackRegistry : IRenderPackRegistry, IDisposable
{
    private readonly IRenderPackRegistry _inner;
    private readonly object _gate = new();
    private readonly List<RegistrationHandle> _registrations = [];
    private bool _disposed;

    internal ScopedRenderPackRegistry(IRenderPackRegistry inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IDisposable Register(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);

        lock (_gate)
            ObjectDisposedException.ThrowIf(_disposed, this);

        IDisposable innerRegistration = _inner.Register(descriptor, assets)
            ?? throw new InvalidOperationException(
                "The render-pack registry returned a null registration handle.");
        var registration = new RegistrationHandle(this, innerRegistration);
        lock (_gate)
        {
            if (!_disposed)
            {
                _registrations.Add(registration);
                return registration;
            }
        }

        registration.Dispose();
        throw new ObjectDisposedException(nameof(ScopedRenderPackRegistry));
    }

    public void Dispose()
    {
        RegistrationHandle[] registrations;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }

        List<Exception>? failures = null;
        for (int index = registrations.Length - 1; index >= 0; index--)
        {
            try { registrations[index].Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more render-pack registrations could not be withdrawn.",
                failures);
        }
    }

    private void Release(RegistrationHandle registration)
    {
        lock (_gate)
            _registrations.Remove(registration);
        registration.DisposeInner();
    }

    private sealed class RegistrationHandle(
        ScopedRenderPackRegistry owner,
        IDisposable inner) : IDisposable
    {
        private ScopedRenderPackRegistry? _owner = owner;
        private IDisposable? _inner = inner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(this);
        }

        internal void DisposeInner() =>
            Interlocked.Exchange(ref _inner, null)?.Dispose();
    }
}
