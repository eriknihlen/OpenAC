using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Lower values win arbitration. A behavior at a lower value preempts one at a
/// higher value the moment it wants control; the reverse waits for the
/// running behavior to finish its step.
/// </summary>
public enum BehaviorPriority
{
    Survival = 0,
    Buffing = 10,
    Combat = 20,
    Looting = 30,
    /// <summary>A closed door in the way of the walk, opened before the walk continues.</summary>
    Doors = 35,
    Navigation = 40,
}

public enum StepResult
{
    /// <summary>Keep control next tick.</summary>
    Continue = 0,
    /// <summary>Finished this unit of work; re-arbitrate next tick.</summary>
    Done,
    /// <summary>Gave up on this unit of work; re-arbitrate and log the reason.</summary>
    Failed,
}

public readonly record struct BehaviorStep(StepResult Result, string? Reason = null)
{
    public static BehaviorStep Continue { get; } = new(StepResult.Continue);
    public static BehaviorStep Done { get; } = new(StepResult.Done);
    public static BehaviorStep Fail(string reason) => new(StepResult.Failed, reason);
}

public sealed class BehaviorContext(
    IAutomationSurface surface,
    IPluginLogger log,
    Blackboard board)
{
    public IAutomationSurface Surface { get; } = surface;
    public IPluginLogger Log { get; } = log;
    public Blackboard Board { get; } = board;
}

public interface IBehavior
{
    string Name { get; }

    BehaviorPriority Priority { get; }

    /// <summary>Whether this behavior has work to do given the snapshot.</summary>
    bool WantsControl(Blackboard board, out string reason);

    /// <summary>One tick of work. Called only while this behavior holds control.</summary>
    BehaviorStep Execute(BehaviorContext context);

    /// <summary>Control was taken away or the bot stopped; release any host state.</summary>
    void Interrupt(BehaviorContext context);
}
