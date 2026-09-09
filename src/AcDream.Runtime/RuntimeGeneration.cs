namespace AcDream.Runtime;

public readonly record struct RuntimeGenerationToken(ulong Value)
{
    public static RuntimeGenerationToken Initial => default;

    public RuntimeGenerationToken Next() => new(checked(Value + 1UL));

    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}

public enum RuntimeLifecycleState
{
    Constructed,
    Stopped,
    Starting,
    InWorld,
    Stopping,
    Faulted,
    Disposed,
}

public readonly record struct RuntimeLifecycleSnapshot(
    RuntimeGenerationToken Generation,
    RuntimeLifecycleState State,
    uint PlayerGuid,
    bool HasTransport);

[Flags]
public enum RuntimeTeardownStage
{
    None = 0,
    CommandsInert = 1 << 0,
    InboundDetached = 1 << 1,
    TransportDisposed = 1 << 2,
    HostReset = 1 << 3,
    Complete = CommandsInert | InboundDetached | TransportDisposed | HostReset,
}

public readonly record struct RuntimeTeardownAcknowledgement(
    RuntimeGenerationToken RetiredGeneration,
    RuntimeGenerationToken CurrentGeneration,
    RuntimeCommandStatus Status,
    RuntimeTeardownStage CompletedStages,
    Exception? Error = null)
{
    public bool IsComplete =>
        Status == RuntimeCommandStatus.Accepted
        &&
        (CompletedStages & RuntimeTeardownStage.Complete)
        == RuntimeTeardownStage.Complete
        && Error is null;
}
