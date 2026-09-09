namespace AcDream.Core.Physics;

public readonly record struct RetailPhysicsStateTransition(
    PhysicsStateFlags PreviousState,
    PhysicsStateFlags RequestedState,
    PhysicsStateFlags FinalState,
    bool LightingChanged,
    bool NoDrawChanged,
    RetailHiddenTransition HiddenTransition)
{
    public bool HasChanges => PreviousState != FinalState;
}

public enum RetailHiddenTransition
{
    None,
    BecameHidden,
    BecameVisible,
}

public static class RetailPhysicsStateTransitions
{
    public const PhysicsStateFlags ConstructorState =
        PhysicsStateFlags.EdgeSlide
        | PhysicsStateFlags.Lighting
        | PhysicsStateFlags.Gravity
        | PhysicsStateFlags.ReportCollisions;

    public static RetailPhysicsStateTransition Apply(
        PhysicsStateFlags previousState,
        PhysicsStateFlags requestedState)
    {
        uint changedLow = ((uint)previousState ^ (uint)requestedState) & 0xFFFFu;
        bool lightingChanged =
            (changedLow & (uint)PhysicsStateFlags.Lighting) != 0;
        bool noDrawChanged =
            (changedLow & (uint)PhysicsStateFlags.NoDraw) != 0;
        bool hiddenChanged =
            (changedLow & (uint)PhysicsStateFlags.Hidden) != 0;

        PhysicsStateFlags finalState = requestedState;
        RetailHiddenTransition hiddenTransition = RetailHiddenTransition.None;
        if (hiddenChanged)
        {
            if ((requestedState & PhysicsStateFlags.Hidden) != 0)
            {
                finalState &= ~PhysicsStateFlags.ReportCollisions;
                finalState |= PhysicsStateFlags.Hidden
                    | PhysicsStateFlags.IgnoreCollisions;
                hiddenTransition = RetailHiddenTransition.BecameHidden;
            }
            else
            {
                finalState &= ~(PhysicsStateFlags.Hidden
                    | PhysicsStateFlags.IgnoreCollisions);
                finalState |= PhysicsStateFlags.ReportCollisions;
                hiddenTransition = RetailHiddenTransition.BecameVisible;
            }
        }

        return new RetailPhysicsStateTransition(
            previousState,
            requestedState,
            finalState,
            lightingChanged,
            noDrawChanged,
            hiddenTransition);
    }
}
