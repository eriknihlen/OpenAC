using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.App.Plugins;

internal sealed class BufferedRenderPackRegistry : IRenderPackRegistry, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Registration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    private long _nextRegistrationId;
    private bool _disposed;

    internal long Revision
    {
        get
        {
            lock (_sync)
                return _revision;
        }
    }

    internal event Action<long>? Changed;

    internal IReadOnlyList<BufferedRenderPackRegistration> Snapshot()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _registrations.Values
                .OrderBy(static value => value.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static value => new BufferedRenderPackRegistration(
                    value.Descriptor,
                    value.Assets,
                    value.RegistrationId))
                .ToArray();
        }
    }

    public IDisposable Register(
        RenderPackDescriptor descriptor,
        IRenderPackAssets assets)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);

        Registration registration;
        long revision;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registrations.ContainsKey(descriptor.Id))
            {
                throw new InvalidOperationException(
                    $"A render pack with id '{descriptor.Id}' is already registered.");
            }

            registration = new Registration(
                this,
                descriptor,
                assets,
                checked(++_nextRegistrationId));
            _registrations.Add(descriptor.Id, registration);
            revision = checked(++_revision);
        }

        PublishChanged(revision);
        return registration;
    }

    public void Dispose()
    {
        Registration[] registrations;
        long? revision = null;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            registrations = _registrations.Values.ToArray();
            _registrations.Clear();
            if (registrations.Length != 0)
                revision = checked(++_revision);
        }

        foreach (Registration registration in registrations)
            registration.WithdrawFromOwner();
        if (revision is { } changedRevision)
            PublishChanged(changedRevision);
    }

    private void Withdraw(Registration registration)
    {
        long? revision = null;
        lock (_sync)
        {
            if (_registrations.TryGetValue(
                    registration.Descriptor.Id,
                    out Registration? active)
                && ReferenceEquals(active, registration))
            {
                _registrations.Remove(registration.Descriptor.Id);
                revision = checked(++_revision);
            }
        }

        if (revision is { } changedRevision)
            PublishChanged(changedRevision);
    }

    private void PublishChanged(long revision)
    {
        Delegate[] subscribers = Changed?.GetInvocationList() ?? [];
        foreach (Delegate subscriber in subscribers)
        {
            try { ((Action<long>)subscriber)(revision); }
            catch
            {
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private BufferedRenderPackRegistry? _owner;

        internal Registration(
            BufferedRenderPackRegistry owner,
            RenderPackDescriptor descriptor,
            IRenderPackAssets assets,
            long registrationId)
        {
            _owner = owner;
            Descriptor = descriptor;
            Assets = assets;
            RegistrationId = registrationId;
        }

        internal RenderPackDescriptor Descriptor { get; }

        internal IRenderPackAssets Assets { get; }

        internal long RegistrationId { get; }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Withdraw(this);

        internal void WithdrawFromOwner() =>
            Interlocked.Exchange(ref _owner, null);
    }
}

internal sealed record BufferedRenderPackRegistration(
    RenderPackDescriptor Descriptor,
    IRenderPackAssets Assets,
    long RegistrationId);
