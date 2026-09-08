using AcDream.Core.Physics;

namespace AcDream.Core.Physics.Motion;

public sealed class MovementParameters
{

    /// <summary>Mask 0x1 — default true.</summary>
    public bool CanWalk { get; set; } = true;

    /// <summary>Mask 0x2 — default true.</summary>
    public bool CanRun { get; set; } = true;

    /// <summary>Mask 0x4 — default true.</summary>
    public bool CanSidestep { get; set; } = true;

    /// <summary>Mask 0x8 — default true.</summary>
    public bool CanWalkBackwards { get; set; } = true;

    public bool CanCharge { get; set; }

    /// <summary>Mask 0x20 — default false.</summary>
    public bool FailWalk { get; set; }

    /// <summary>Mask 0x40 — default false.</summary>
    public bool UseFinalHeading { get; set; }

    /// <summary>Mask 0x80 — default false.</summary>
    public bool Sticky { get; set; }

    /// <summary>Mask 0x100 — default false.</summary>
    public bool MoveAway { get; set; }

    /// <summary>Mask 0x200 — default true.</summary>
    public bool MoveTowards { get; set; } = true;

    /// <summary>Mask 0x400 — default true.</summary>
    public bool UseSpheres { get; set; } = true;

    public bool SetHoldKey { get; set; } = true;

    public bool Autonomous { get; set; }

    /// <summary>Mask 0x2000 — default true. DoMotion @306213: byte1&amp;0x20
    /// mirrors the applied motion into <c>RawMotionState</c> via
    /// <c>ApplyMotion</c>/<c>RemoveMotion</c>.</summary>
    public bool ModifyRawState { get; set; } = true;

    /// <summary>Mask 0x4000 — default true. Mirrors into
    /// <c>InterpretedMotionState</c>.</summary>
    public bool ModifyInterpretedState { get; set; } = true;

    public bool CancelMoveTo { get; set; } = true;

    public bool StopCompletelyFlag { get; set; } = true;

    public bool DisableJumpDuringLink { get; set; }


    public float DistanceToObject { get; set; } = 0.6f;

    public float MinDistance { get; set; }

    public float DesiredHeading { get; set; }

    public float Speed { get; set; } = 1f;

    public float FailDistance { get; set; } = float.MaxValue;

    public float WalkRunThreshhold { get; set; } = 15f;

    public uint ContextId { get; set; }

    public HoldKey HoldKeyToApply { get; set; } = HoldKey.Invalid;

    public uint ActionStamp { get; set; }


    public void GetCommand(float dist, float headingDiff, out uint motion, out HoldKey holdKey, out bool movingAway)
    {
        _ = headingDiff;

        if (MoveTowards && MoveAway)
        {
            TowardsAndAway(dist, out motion, out movingAway);
        }
        else if (MoveAway && !MoveTowards)
        {
            // pure AWAY: dist < min_distance → WalkForward, moving away
            // (turn-around; heading flips +180 via GetDesiredHeading).
            if (dist < MinDistance)
            {
                motion = MotionCommand.WalkForward;
                movingAway = true;
            }
            else
            {
                motion = 0u;
                movingAway = false;
            }
        }
        else
        {
            if (dist > DistanceToObject)
            {
                motion = MotionCommand.WalkForward;
                movingAway = false;
            }
            else
            {
                motion = 0u;
                movingAway = false;
            }
        }

        if (CanCharge)
        {
            holdKey = HoldKey.Run;
            return;
        }
        if (!CanRun)
        {
            holdKey = HoldKey.None;
            return;
        }
        if (CanWalk && (dist - DistanceToObject) <= WalkRunThreshhold)
        {
            holdKey = HoldKey.None;
            return;
        }
        holdKey = HoldKey.Run;
    }

    public void TowardsAndAway(float dist, out uint cmd, out bool movingAway)
    {
        const float epsilon = 0.000199999995f;

        if (dist > DistanceToObject)
        {
            cmd = MotionCommand.WalkForward;
            movingAway = false;
            return;
        }
        if (dist - MinDistance < epsilon)
        {
            cmd = MotionCommand.WalkBackward;
            movingAway = true;
            return;
        }
        cmd = 0u;
        movingAway = false;
    }

    public float GetDesiredHeading(uint command, bool movingAway)
    {
        if (command == MotionCommand.RunForward || command == MotionCommand.WalkForward)
        {
            if (!movingAway) return 0f;
        }
        else
        {
            if (command != MotionCommand.WalkBackward) return 0f;
            if (movingAway) return 0f;
        }
        return 180f;
    }


    public static MovementParameters FromWire(
        uint bitfield,
        float distanceToObject,
        float minDistance,
        float failDistance,
        float speed,
        float walkRunThreshhold,
        float desiredHeading)
    {
        var p = new MovementParameters();
        ApplyBitfield(p, bitfield);
        p.DistanceToObject = distanceToObject;
        p.MinDistance = minDistance;
        p.FailDistance = failDistance;
        p.Speed = speed;
        p.WalkRunThreshhold = walkRunThreshhold;
        p.DesiredHeading = desiredHeading;
        return p;
    }

    public static MovementParameters FromWireTurnTo(
        uint bitfield,
        float speed,
        float desiredHeading)
    {
        var p = new MovementParameters();
        ApplyBitfield(p, bitfield);
        p.Speed = speed;
        p.DesiredHeading = desiredHeading;
        return p;
    }

    private static void ApplyBitfield(MovementParameters p, uint bitfield)
    {
        p.CanWalk = (bitfield & 0x1u) != 0;
        p.CanRun = (bitfield & 0x2u) != 0;
        p.CanSidestep = (bitfield & 0x4u) != 0;
        p.CanWalkBackwards = (bitfield & 0x8u) != 0;
        p.CanCharge = (bitfield & 0x10u) != 0;
        p.FailWalk = (bitfield & 0x20u) != 0;
        p.UseFinalHeading = (bitfield & 0x40u) != 0;
        p.Sticky = (bitfield & 0x80u) != 0;
        p.MoveAway = (bitfield & 0x100u) != 0;
        p.MoveTowards = (bitfield & 0x200u) != 0;
        p.UseSpheres = (bitfield & 0x400u) != 0;
        p.SetHoldKey = (bitfield & 0x800u) != 0;
        p.Autonomous = (bitfield & 0x1000u) != 0;
        p.ModifyRawState = (bitfield & 0x2000u) != 0;
        p.ModifyInterpretedState = (bitfield & 0x4000u) != 0;
        p.CancelMoveTo = (bitfield & 0x8000u) != 0;
        p.StopCompletelyFlag = (bitfield & 0x10000u) != 0;
        p.DisableJumpDuringLink = (bitfield & 0x20000u) != 0;
    }
}
