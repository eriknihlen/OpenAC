namespace AcDream.App.Net;

using AcDream.Runtime;

/// <summary>One named host operation around Runtime's generation reset.</summary>
internal sealed record LiveSessionResetStage(
    string Name,
    Action<RuntimeGenerationToken> Reset)
{
    public LiveSessionResetStage(string name, Action reset)
        : this(
            name,
            _ => (reset ?? throw new ArgumentNullException(nameof(reset)))())
    {
    }
}

internal sealed class LiveSessionResetPlan
{
    private readonly LiveSessionResetStage[] _stages;
    private int _executing;

    public LiveSessionResetPlan(IEnumerable<LiveSessionResetStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        _stages = stages.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (LiveSessionResetStage stage in _stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            if (string.IsNullOrWhiteSpace(stage.Name))
                throw new ArgumentException("Reset stage names must be non-empty.", nameof(stages));
            ArgumentNullException.ThrowIfNull(stage.Reset);
            if (!names.Add(stage.Name))
                throw new ArgumentException(
                    $"Duplicate live-session reset stage '{stage.Name}'.",
                    nameof(stages));
        }
    }

    public IReadOnlyList<string> StageNames =>
        Array.ConvertAll(_stages, static stage => stage.Name);

    public void Execute()
        => Execute(default);

    public void Execute(RuntimeGenerationToken retiringGeneration)
    {
        if (Interlocked.Exchange(ref _executing, 1) != 0)
            throw new InvalidOperationException(
                "Live-session reset cannot run concurrently or reentrantly.");

        List<Exception>? failures = null;
        try
        {
            foreach (LiveSessionResetStage stage in _stages)
            {
                try
                {
                    stage.Reset(retiringGeneration);
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(new LiveSessionResetStageException(
                        stage.Name,
                        error));
                }
            }
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }

        if (failures is not null)
            throw new AggregateException(
                "Live-session state did not converge; a new session must not start.",
                failures);
    }
}

internal sealed class LiveSessionResetStageException(
    string stageName,
    Exception innerException) : Exception(
        $"Live-session reset stage '{stageName}' failed.",
        innerException)
{
    public string StageName { get; } = stageName;
}
