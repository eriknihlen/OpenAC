using System;
using System.Numerics;

namespace AcDream.Core.Physics.Motion;

public sealed class ConstraintManager
{
    private readonly IPhysicsObjHost _host;

    public bool IsConstrained { get; private set; }

    public float ConstraintPosOffset { get; private set; }

    public Position ConstraintPos { get; private set; }

    public float ConstraintDistanceStart { get; private set; }

    public float ConstraintDistanceMax { get; private set; }

    public ConstraintManager(IPhysicsObjHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public void ConstrainTo(Position anchor, float startDistance, float maxDistance)
    {
        IsConstrained = true;
        ConstraintPos = anchor;
        ConstraintDistanceStart = startDistance;
        ConstraintDistanceMax = maxDistance;
        ConstraintPosOffset = Vector3.Distance(anchor.Frame.Origin, _host.Position.Frame.Origin);
    }

    public void UnConstrain() => IsConstrained = false;

    public bool IsFullyConstrained()
        => ConstraintDistanceMax * 0.9f < ConstraintPosOffset;

    public void AdjustOffset(MotionDeltaFrame offset, double quantum)
    {
        _ = quantum;
        if (!IsConstrained)
            return;

        if (_host.InContact)
        {
            if (ConstraintPosOffset < ConstraintDistanceMax)
            {
                if (ConstraintPosOffset > ConstraintDistanceStart)
                {
                    // Linear brake taper: 1.0 just past start → 0.0 at max.
                    float taper = (ConstraintDistanceMax - ConstraintPosOffset)
                                / (ConstraintDistanceMax - ConstraintDistanceStart);
                    offset.Origin *= taper;
                }
            }
            else
            {
                offset.Origin = Vector3.Zero; // past max — fully pinned.
            }
        }

        // Unconditional (grounded OR airborne): track this tick's step length.
        ConstraintPosOffset = offset.Origin.Length();
    }
}
