using System;

namespace AcDream.Core.Physics.Motion;

public sealed class PositionManager
{
    private readonly IPhysicsObjHost _host;

    private StickyManager? _sticky;
    private ConstraintManager? _constraint;

    public PositionManager(IPhysicsObjHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public StickyManager? Sticky => _sticky;
    public ConstraintManager? Constraint => _constraint;

    public void StickTo(uint objectId, float radius, float height)
    {
        _sticky ??= new StickyManager(_host);
        _sticky.StickTo(objectId, radius, height);
    }

    public void UnStick() => _sticky?.UnStick();

    public uint GetStickyObjectId() => _sticky?.TargetId ?? 0u;

    public void ConstrainTo(Position anchor, float startDistance, float maxDistance)
    {
        _constraint ??= new ConstraintManager(_host);
        _constraint.ConstrainTo(anchor, startDistance, maxDistance);
    }

    public void UnConstrain() => _constraint?.UnConstrain();

    public bool IsFullyConstrained() => _constraint?.IsFullyConstrained() ?? false;

    public void HandleUpdateTarget(TargetInfo info) => _sticky?.HandleUpdateTarget(info);

    public void AdjustOffset(MotionDeltaFrame offset, double quantum)
    {
        _sticky?.AdjustOffset(offset, quantum);
        _constraint?.AdjustOffset(offset, quantum);
    }

    public void UseTime() => _sticky?.UseTime();
}
