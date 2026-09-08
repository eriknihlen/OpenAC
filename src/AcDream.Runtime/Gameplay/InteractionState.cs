namespace AcDream.Runtime.Gameplay;

public enum InteractionModeKind
{
    None,
    Use,
    Examine,
    UseItemOnTarget,
}

public readonly record struct InteractionMode(
    InteractionModeKind Kind,
    uint SourceObjectId = 0)
{
    public static InteractionMode None => new(InteractionModeKind.None);
}

public readonly record struct InteractionModeTransition(
    InteractionMode Previous,
    InteractionMode Current);

public sealed class InteractionState
{
    public InteractionMode Current { get; private set; } = InteractionMode.None;

    public event Action<InteractionModeTransition>? Changed;

    public bool EnterUse() => Set(new InteractionMode(InteractionModeKind.Use));

    public bool EnterExamine() =>
        Set(new InteractionMode(InteractionModeKind.Examine));

    public bool EnterUseItemOnTarget(uint sourceObjectId)
    {
        if (sourceObjectId == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceObjectId));
        return Set(new InteractionMode(
            InteractionModeKind.UseItemOnTarget,
            sourceObjectId));
    }

    public bool Clear() => Set(InteractionMode.None);

    public void ResetSession()
    {
        InteractionMode previous = Current;
        Current = InteractionMode.None;
        Action<InteractionModeTransition>? listeners = Changed;
        if (listeners is null)
            return;

        var transition = new InteractionModeTransition(
            previous,
            InteractionMode.None);
        List<Exception>? failures = null;
        foreach (Action<InteractionModeTransition> listener
            in listeners.GetInvocationList())
        {
            try
            {
                listener(transition);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more interaction-mode reset observers failed.",
                failures);
        }
    }

    private bool Set(InteractionMode mode)
    {
        if (Current == mode)
            return false;
        InteractionMode previous = Current;
        Current = mode;
        Changed?.Invoke(new InteractionModeTransition(previous, mode));
        return true;
    }
}
