namespace AcDream.App.Rendering;

internal enum ResourceShutdownOperationPolicy
{
    HardBarrier,
    ReportAndContinue,
}

internal readonly record struct ResourceShutdownOperation(
    string Name,
    Action Execute,
    ResourceShutdownOperationPolicy Policy =
        ResourceShutdownOperationPolicy.HardBarrier);

internal sealed record ResourceShutdownCleanupFailure(
    string Stage,
    string Operation,
    Exception Error);

internal sealed record ResourceShutdownStage(
    string Name,
    ResourceShutdownOperation[] Operations);

internal sealed class ResourceShutdownTransaction(params ResourceShutdownStage[] stages)
{
    private sealed class StageState(int operationCount)
    {
        public bool[] Complete { get; } = new bool[operationCount];
        public int[] Attempts { get; } = new int[operationCount];
    }

    private const int MaximumConsecutiveStalledPasses = 2;
    private readonly ResourceShutdownStage[] _stages =
        stages ?? throw new ArgumentNullException(nameof(stages));
    private readonly StageState[] _states =
        stages.Select(stage => new StageState(stage.Operations.Length)).ToArray();
    private readonly List<ResourceShutdownCleanupFailure> _cleanupFailures = [];
    private int _currentStage;
    private bool _completing;

    public bool IsComplete => _currentStage == _stages.Length;
    internal int CurrentStage => _currentStage;
    internal string? CurrentStageName => IsComplete ? null : _stages[_currentStage].Name;
    internal IReadOnlyList<ResourceShutdownCleanupFailure> CleanupFailures =>
        _cleanupFailures.ToArray();

    public void CompleteOrThrow()
    {
        if (_completing || IsComplete)
            return;

        _completing = true;
        try
        {
            int stalledPasses = 0;
            List<Exception>? latestFailures = null;
            while (!IsComplete)
            {
                bool progressed = AdvanceCurrentStage(out List<Exception>? failures);
                latestFailures = failures;
                if (progressed)
                {
                    stalledPasses = 0;
                    continue;
                }

                stalledPasses++;
                if (stalledPasses < MaximumConsecutiveStalledPasses)
                    continue;

                ResourceShutdownStage stage = _stages[_currentStage];
                throw new AggregateException(
                    $"Shutdown stage '{stage.Name}' did not converge after retrying its pending operations.",
                    latestFailures ??
                    [new InvalidOperationException("The shutdown stage made no progress and reported no failure.")]);
            }
        }
        finally
        {
            _completing = false;
        }
    }

    private bool AdvanceCurrentStage(out List<Exception>? failures)
    {
        ResourceShutdownStage stage = _stages[_currentStage];
        StageState state = _states[_currentStage];
        bool progressed = false;
        failures = null;

        for (int i = 0; i < stage.Operations.Length; i++)
        {
            if (state.Complete[i])
                continue;

            ResourceShutdownOperation operation = stage.Operations[i];
            try
            {
                operation.Execute();
                state.Complete[i] = true;
                progressed = true;
            }
            catch (Exception error)
            {
                var wrapped = new InvalidOperationException(
                    $"Shutdown operation '{operation.Name}' failed in stage '{stage.Name}'.",
                    error);
                state.Attempts[i]++;
                if (operation.Policy ==
                        ResourceShutdownOperationPolicy.ReportAndContinue
                    && state.Attempts[i] >= MaximumConsecutiveStalledPasses)
                {
                    state.Complete[i] = true;
                    progressed = true;
                    _cleanupFailures.Add(new ResourceShutdownCleanupFailure(
                        stage.Name,
                        operation.Name,
                        error));
                }
                else
                {
                    (failures ??= []).Add(wrapped);
                }
            }
        }

        if (state.Complete.All(static complete => complete))
        {
            _currentStage++;
            progressed = true;
        }

        return progressed;
    }
}
