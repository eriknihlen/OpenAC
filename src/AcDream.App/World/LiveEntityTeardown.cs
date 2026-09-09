namespace AcDream.App.World;

internal static class LiveEntityTeardown
{
    internal static void Run(IEnumerable<Action> cleanups)
    {
        ArgumentNullException.ThrowIfNull(cleanups);
        List<Exception>? failures = null;
        foreach (Action cleanup in cleanups)
        {
            try
            {
                cleanup();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more live-entity component teardown steps failed.",
                failures);
    }
}

internal sealed class LiveEntityTeardownPlan
{
    private readonly Action[] _steps;
    private readonly bool[] _completed;

    public LiveEntityTeardownPlan(IEnumerable<Action> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = [.. steps];
        _completed = new bool[_steps.Length];
    }

    internal int CompletedCount => _completed.Count(static completed => completed);
    internal bool IsComplete => CompletedCount == _steps.Length;

    public void Advance()
    {
        List<Exception>? failures = null;
        for (int i = 0; i < _steps.Length; i++)
        {
            if (_completed[i])
                continue;
            try
            {
                _steps[i]();
                _completed[i] = true;
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
            throw new AggregateException(
                "One or more live-entity component teardown steps failed.",
                failures);
    }
}
