namespace AcDream.App.Rendering;

using System.Runtime.ExceptionServices;

internal interface IRetryableResourceCleanup
{
    bool IsCleanupComplete { get; }
    void RetryCleanup();
}

internal sealed class ResourceConstructionException : AggregateException,
    IRetryableResourceCleanup
{
    private readonly IRetryableResourceCleanup _cleanup;

    public ResourceConstructionException(
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

internal sealed class ResourceCleanupGroup : IRetryableResourceCleanup
{
    private sealed record Entry(string Name, Action Release)
    {
        public bool Complete { get; set; }
    }

    private readonly List<Entry> _entries = [];
    private bool _running;

    public bool IsCleanupComplete => _entries.All(static entry => entry.Complete);

    public void Add(string name, Action release)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(release);
        if (_running || IsCleanupComplete && _entries.Count != 0)
            throw new InvalidOperationException("The resource cleanup group is no longer accepting ownership.");
        _entries.Add(new Entry(name, release));
    }

    public void TransferAll()
    {
        if (_running)
            throw new InvalidOperationException(
                "The resource cleanup group is currently releasing resources.");
        foreach (Entry entry in _entries)
            entry.Complete = true;
    }

    public void RetryCleanup()
    {
        if (_running || IsCleanupComplete)
            return;

        _running = true;
        List<Exception>? failures = null;
        try
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Complete)
                    continue;
                try
                {
                    entry.Release();
                    entry.Complete = true;
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(new InvalidOperationException(
                        $"Resource cleanup operation '{entry.Name}' failed.",
                        failure));
                }
            }
        }
        finally
        {
            _running = false;
        }

        if (failures is not null)
            throw new AggregateException("Composite resource cleanup remains incomplete.", failures);
    }

    public void RollbackConstructionAndThrow(
        string message,
        Exception constructionFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(constructionFailure);
        try
        {
            RetryCleanup();
        }
        catch (Exception cleanupFailure)
        {
            throw new ResourceConstructionException(
                message,
                this,
                [constructionFailure, cleanupFailure]);
        }

        ExceptionDispatchInfo.Capture(constructionFailure).Throw();
        throw new InvalidOperationException("Unreachable construction rollback path.");
    }
}

internal sealed class ResourceConstructionCleanupLedger : IDisposable
{
    private readonly List<IRetryableResourceCleanup> _pending = [];
    private bool _disposing;

    public bool IsComplete => _pending.Count == 0;

    public bool RetainFrom(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        bool retained = false;
        Visit(failure);
        return retained;

        void Visit(Exception current)
        {
            if (current is IRetryableResourceCleanup cleanup)
            {
                if (!cleanup.IsCleanupComplete && !_pending.Contains(cleanup))
                    _pending.Add(cleanup);
                retained = true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    Visit(inner);
            }
            else if (current.InnerException is { } inner)
            {
                Visit(inner);
            }
        }
    }

    public void Dispose()
    {
        if (_disposing || _pending.Count == 0)
            return;

        _disposing = true;
        List<Exception>? failures = null;
        try
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                IRetryableResourceCleanup cleanup = _pending[i];
                try
                {
                    cleanup.RetryCleanup();
                    if (cleanup.IsCleanupComplete)
                        _pending.RemoveAt(i);
                }
                catch (Exception failure)
                {
                    (failures ??= []).Add(failure);
                }
            }
        }
        finally
        {
            _disposing = false;
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more failed resource construction transactions remain pending.",
                failures);
        }
    }
}
