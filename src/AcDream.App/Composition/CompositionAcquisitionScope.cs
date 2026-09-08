using System.Runtime.ExceptionServices;
using AcDream.App.Rendering;

namespace AcDream.App.Composition;

internal sealed class CompositionAcquisitionScope : IRetryableResourceCleanup
{
    internal enum EntryState
    {
        Owned,
        Transferred,
        Released,
    }

    internal sealed class Entry(string name, object resource, Action release)
    {
        public string Name { get; } = name;
        public object Resource { get; } = resource;
        public Action Release { get; } = release;
        public EntryState State { get; set; } = EntryState.Owned;
    }

    private readonly List<Entry> _entries = [];
    private bool _cleanupActive;
    private bool _closed;

    public bool IsCleanupComplete =>
        _entries.All(static entry => entry.State is not EntryState.Owned);

    public CompositionAcquisitionLease<T> Acquire<T>(
        string name,
        Func<T> factory,
        Action<T> release)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(release);
        EnsureAcceptingOwnership();

        T resource = factory()
            ?? throw new InvalidOperationException(
                $"Composition factory '{name}' returned null.");
        return Own(name, resource, release);
    }

    public CompositionAcquisitionOptionalLease<T> AcquireOptional<T>(
        string name,
        Func<T?> factory,
        Action<T> release)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(release);
        EnsureAcceptingOwnership();

        T? resource = factory();
        return resource is null
            ? new CompositionAcquisitionOptionalLease<T>(null)
            : new CompositionAcquisitionOptionalLease<T>(
                Own(name, resource, release));
    }

    public CompositionAcquisitionLease<T> Own<T>(
        string name,
        T resource,
        Action<T> release)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(release);
        EnsureAcceptingOwnership();

        var entry = new Entry(name, resource, () => release(resource));
        _entries.Add(entry);
        return new CompositionAcquisitionLease<T>(
            this,
            entry,
            resource);
    }

    public void Complete()
    {
        if (_cleanupActive)
            throw new InvalidOperationException(
                "Composition cleanup is currently active.");
        if (_closed)
            return;
        if (!IsCleanupComplete)
        {
            string pending = string.Join(
                ", ",
                _entries
                    .Where(static entry => entry.State == EntryState.Owned)
                    .Select(static entry => entry.Name));
            throw new InvalidOperationException(
                $"Composition phase completed with unpublished resources: {pending}.");
        }

        _closed = true;
    }

    public void RollbackAndThrow(Exception constructionFailure)
    {
        ArgumentNullException.ThrowIfNull(constructionFailure);
        _closed = true;

        try
        {
            RetryCleanup();
        }
        catch (AggregateException cleanupFailure)
        {
            var failures = new List<Exception> { constructionFailure };
            failures.AddRange(cleanupFailure.InnerExceptions);
            throw new CompositionAcquisitionException(
                "Startup composition failed and rollback remains incomplete.",
                this,
                failures);
        }

        ExceptionDispatchInfo.Capture(constructionFailure).Throw();
    }

    public void RetryCleanup()
    {
        if (_cleanupActive || IsCleanupComplete)
            return;

        _closed = true;
        _cleanupActive = true;
        List<Exception>? failures = null;
        try
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.State != EntryState.Owned)
                    continue;

                try
                {
                    entry.Release();
                    entry.State = EntryState.Released;
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(new InvalidOperationException(
                        $"Composition cleanup operation '{entry.Name}' failed.",
                        failure));
                }
            }
        }
        finally
        {
            _cleanupActive = false;
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Startup composition rollback remains incomplete.",
                failures);
        }
    }

    private void EnsureAcceptingOwnership()
    {
        if (_closed || _cleanupActive)
            throw new InvalidOperationException(
                "The composition acquisition scope is no longer accepting ownership.");
    }

    private void Transfer<T>(Entry entry, T expected)
        where T : class
    {
        if (_closed || _cleanupActive)
            throw new InvalidOperationException(
                "The composition acquisition scope is no longer transferable.");
        if (!ReferenceEquals(entry.Resource, expected))
            throw new InvalidOperationException(
                "The acquisition lease does not own the expected resource.");
        if (entry.State != EntryState.Owned)
            throw new InvalidOperationException(
                $"Composition resource '{entry.Name}' has already been transferred.");

        entry.State = EntryState.Transferred;
    }

    internal sealed class CompositionAcquisitionLease<T>
        where T : class
    {
        private readonly CompositionAcquisitionScope _scope;
        private readonly Entry _entry;

        internal CompositionAcquisitionLease(
            CompositionAcquisitionScope scope,
            Entry entry,
            T resource)
        {
            _scope = scope;
            _entry = entry;
            Resource = resource;
        }

        public T Resource { get; }

        public T Transfer()
        {
            _scope.Transfer(_entry, Resource);
            return Resource;
        }

        public T Publish(Action<T> publish)
        {
            ArgumentNullException.ThrowIfNull(publish);
            publish(Resource);
            return Transfer();
        }
    }

    /// <summary>A lease over a resource the active backend may not own at all.</summary>
    internal sealed class CompositionAcquisitionOptionalLease<T>(
        CompositionAcquisitionLease<T>? inner)
        where T : class
    {
        public T? Resource => inner?.Resource;

        public T? Transfer() => inner?.Transfer();

        public T? Publish(Action<T?> publish)
        {
            ArgumentNullException.ThrowIfNull(publish);
            if (inner is null)
            {
                publish(null);
                return null;
            }

            publish(inner.Resource);
            return inner.Transfer();
        }
    }
}

internal sealed class CompositionAcquisitionException : AggregateException,
    IRetryableResourceCleanup
{
    private readonly IRetryableResourceCleanup _cleanup;

    public CompositionAcquisitionException(
        string message,
        IRetryableResourceCleanup cleanup,
        IEnumerable<Exception> failures)
        : base(message, failures)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
    }

    public bool IsCleanupComplete => _cleanup.IsCleanupComplete;

    public void RetryCleanup() => _cleanup.RetryCleanup();
}
