using System;

namespace AcDream.Core.Physics.Motion;

public sealed class MotionTableDispatchSink : IInterpretedMotionSink
{
    private readonly AnimationSequencer _sequencer;

    public MotionTableDispatchSink(AnimationSequencer sequencer)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        _sequencer = sequencer;
    }

    public bool ApplyMotion(uint motion, float speed)
    {
        uint result = _sequencer.PerformMovement(MotionTableMovement.Interpreted(motion, speed));
        return result == MotionTableManagerError.Success;
    }

    public bool StopMotion(uint motion)
    {
        uint result = _sequencer.PerformMovement(MotionTableMovement.StopInterpreted(motion, 1f));
        return result == MotionTableManagerError.Success;
    }

    public bool StopCompletely()
    {
        uint result = _sequencer.PerformMovement(MotionTableMovement.StopCompletely());
        return result == MotionTableManagerError.Success;
    }
}
