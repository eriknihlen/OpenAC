using System;

namespace AcDream.Core.Physics.Motion;

public sealed class MovementManager
{
    public MotionInterpreter Minterp { get; }

    public MoveToManager? MoveTo { get; private set; }

    public Func<MoveToManager>? MoveToFactory { get; set; }

    public Action? ActivatePhysicsObject { get; set; }

    public MovementManager(MotionInterpreter minterp)
    {
        Minterp = minterp ?? throw new ArgumentNullException(nameof(minterp));
    }

    public void MakeMoveToManager()
    {
        if (MoveTo is null && MoveToFactory is not null)
            MoveTo = MoveToFactory();
    }

    public WeenieError PerformMovement(MovementStruct mvs)
    {
        ActivatePhysicsObject?.Invoke();

        switch (mvs.Type)
        {
            case MovementType.RawCommand:
            case MovementType.InterpretedCommand:
            case MovementType.StopRawCommand:
            case MovementType.StopInterpretedCommand:
            case MovementType.StopCompletely:
                return Minterp.PerformMovement(mvs);

            case MovementType.MoveToObject:
            case MovementType.MoveToPosition:
            case MovementType.TurnToObject:
            case MovementType.TurnToHeading:
                MakeMoveToManager();
                if (MoveTo is null)
                    return WeenieError.GeneralMovementFailure;
                MoveTo.PerformMovement(mvs);
                return WeenieError.None;

            default:
                return WeenieError.GeneralMovementFailure; // 0x47
        }
    }

    public void UseTime() => MoveTo?.UseTime();

    public void HitGround()
    {
        Minterp.HitGround();
        MoveTo?.HitGround();
    }

    public void HandleExitWorld() => Minterp.HandleExitWorld();

    public void CancelMoveTo(WeenieError error) => MoveTo?.CancelMoveTo(error);

    public void HandleUpdateTarget(TargetInfo info) => MoveTo?.HandleUpdateTarget(info);

    public bool IsMovingTo() => MoveTo?.IsMovingTo() ?? false;
}
