using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Physics;

public sealed class RuntimePlacementProjectionChannel
{
    private readonly RuntimeEntityObjectEventStream _events;
    private readonly RuntimeSetPositionState _setPosition;
    private readonly RuntimeInitialCreateContinuationExecutor _initialCreateExecution;
    private Func<RuntimeGenerationToken> _generation = static () => default;
    private bool _generationBound;

    internal RuntimePlacementProjectionChannel(
        RuntimeEntityObjectEventStream events,
        RuntimeSetPositionState setPosition,
        RuntimeInitialCreateContinuationExecutor initialCreateExecution)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _setPosition = setPosition
            ?? throw new ArgumentNullException(nameof(setPosition));
        _initialCreateExecution = initialCreateExecution
            ?? throw new ArgumentNullException(nameof(initialCreateExecution));
    }

    public IDisposable Subscribe(IRuntimePlacementObserver observer) =>
        _events.SubscribePlacement(observer);

    internal void BindGeneration(Func<RuntimeGenerationToken> generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (_generationBound)
        {
            throw new InvalidOperationException(
                "The Runtime placement generation source is already bound.");
        }

        _generation = generation;
        _generationBound = true;
    }

    /// <summary>
    /// Acknowledges only the exact oldest outstanding receipt. Stale,
    /// reordered, superseded, or already acknowledged tokens are rejected.
    /// </summary>
    public bool Acknowledge(
        RuntimeGenerationToken expectedGeneration,
        in RuntimePlacementProjectionToken token) =>
        IsCurrent(expectedGeneration)
        && _setPosition.AcknowledgeProjection(token);

    public bool RetryPending(RuntimeGenerationToken expectedGeneration)
    {
        if (!IsCurrent(expectedGeneration))
            return false;
        _setPosition.RetryPendingProjections();
        return true;
    }

    public bool TryPeek(
        RuntimeGenerationToken expectedGeneration,
        out RuntimePlacementProjectionSnapshot projection)
    {
        if (IsCurrent(expectedGeneration))
            return _setPosition.TryPeekProjection(out projection);
        projection = default;
        return false;
    }

    public int PendingCount => _setPosition.PendingProjectionCount;

    public bool TryGetInitialCreateCompletion(
        RuntimeGenerationToken expectedGeneration,
        in RuntimePlacementProjectionToken token,
        out RuntimeInitialCreatePlacementCompletion completion)
    {
        if (IsCurrent(expectedGeneration))
            return _initialCreateExecution.TryGetCompletion(token, out completion);
        completion = default;
        return false;
    }

    private bool IsCurrent(RuntimeGenerationToken expectedGeneration) =>
        _generationBound
        && expectedGeneration.Value != 0UL
        && expectedGeneration == _generation();
}
