using System.Collections.Generic;

namespace AcDream.Core.Physics;

public interface IInterpretedMotionSink
{
    bool ApplyMotion(uint motion, float speed);

    bool StopMotion(uint motion);

    bool StopCompletely() => true;
}

public readonly record struct InboundMotionAction(
    uint Command, int Stamp, bool Autonomous, float Speed);

public struct InboundInterpretedState
{
    public uint CurrentStyle;
    public uint ForwardCommand;
    public float ForwardSpeed;
    public uint SideStepCommand;
    public float SideStepSpeed;
    public uint TurnCommand;
    public float TurnSpeed;
    public IReadOnlyList<InboundMotionAction>? Actions;

    public static InboundInterpretedState Default() => new()
    {
        CurrentStyle    = 0x8000003Du,
        ForwardCommand  = 0x41000003u,
        ForwardSpeed    = 1.0f,
        SideStepCommand = 0u,
        SideStepSpeed   = 1.0f,
        TurnCommand     = 0u,
        TurnSpeed       = 1.0f,
        Actions         = null,
    };
}
