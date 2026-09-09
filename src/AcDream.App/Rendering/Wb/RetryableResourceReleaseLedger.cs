namespace AcDream.App.Rendering.Wb;

internal sealed class RetryableResourceReleaseLedger
{
    private readonly Entry[] _entries;
    private int _remaining;
    private bool _advancing;

    public RetryableResourceReleaseLedger(IEnumerable<(string Name, Action Release)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries
            .Select(entry => new Entry(entry.Name, entry.Release))
            .ToArray();
        _remaining = _entries.Length;
    }

    public bool IsComplete => _remaining == 0;
    public int RemainingCount => _remaining;

    public ResourceReleaseAttempt Advance()
    {
        if (_advancing)
            return new ResourceReleaseAttempt(0, 0, []);

        _advancing = true;
        List<ResourceReleaseFailure>? failures = null;
        int attempted = 0;
        int completed = 0;
        try
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                Entry entry = _entries[i];
                if (entry.Completed || entry.Running)
                    continue;

                attempted++;
                entry.Running = true;
                try
                {
                    entry.Release();
                    entry.Completed = true;
                    _remaining--;
                    completed++;
                }
                catch (Exception error)
                {
                    bool committed = error is MeshReferenceMutationException
                    {
                        MutationCommitted: true,
                    };
                    if (committed)
                    {
                        entry.Completed = true;
                        _remaining--;
                        completed++;
                    }

                    (failures ??= []).Add(
                        new ResourceReleaseFailure(entry.Name, error, committed));
                }
                finally
                {
                    entry.Running = false;
                }
            }
        }
        finally
        {
            _advancing = false;
        }

        return new ResourceReleaseAttempt(
            attempted,
            completed,
            failures ?? []);
    }

    private sealed class Entry
    {
        public Entry(string name, Action release)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A resource-release stage needs a name.", nameof(name));
            ArgumentNullException.ThrowIfNull(release);
            Name = name;
            Release = release;
        }

        public string Name { get; }
        public Action Release { get; }
        public bool Completed { get; set; }
        public bool Running { get; set; }
    }
}

internal readonly record struct ResourceReleaseFailure(
    string Stage,
    Exception Error,
    bool MutationCommitted);

internal readonly record struct ResourceReleaseAttempt(
    int AttemptedCount,
    int CompletedCount,
    IReadOnlyList<ResourceReleaseFailure> Failures)
{
    public bool HasFailures => Failures.Count != 0;

    public AggregateException ToException(string message) =>
        new(
            message,
            Failures.Select(failure =>
                new InvalidOperationException(
                    $"Resource-release stage '{failure.Stage}' failed "
                    + (failure.MutationCommitted
                        ? "after committing its mutation."
                        : "before committing its mutation."),
                    failure.Error)));
}
