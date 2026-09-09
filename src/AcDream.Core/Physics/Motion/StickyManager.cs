using System;
using System.Numerics;

namespace AcDream.Core.Physics.Motion;

public sealed class StickyManager
{
    public const float StickyRadius = 0.3f;

    public const float StickyTime = 1.0f;

    private const float FollowSpeedFactor = 5.0f;

    private const float FallbackFollowSpeed = 15.0f;

    private readonly IPhysicsObjHost _host;

    public uint TargetId { get; private set; }

    public float TargetRadius { get; private set; }

    public Position TargetPosition { get; private set; }

    public bool Initialized { get; private set; }

    public double StickyTimeoutTime { get; private set; }

    public StickyManager(IPhysicsObjHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    public void UnStick()
    {
        if (TargetId == 0)
            return;

        TargetId = 0;
        Initialized = false;
        _host.ClearTarget();
        _host.InterruptCurrentMovement();
    }

    public void StickTo(uint objectId, float targetRadius, float targetHeight)
    {
        _ = targetHeight;

        if (TargetId != 0)
        {
            TargetId = 0;
            Initialized = false;
            _host.ClearTarget();
            _host.InterruptCurrentMovement();
        }

        TargetRadius = targetRadius;
        TargetId = objectId;
        Initialized = false;
        StickyTimeoutTime = _host.CurTime + StickyTime;

        _host.SetTarget(0, objectId, 0.5f, 0.5);
    }

    public void UseTime()
    {
        if (TargetId == 0)
            return;

        if (_host.CurTime > StickyTimeoutTime)
        {
            TargetId = 0;
            Initialized = false;
            _host.ClearTarget();
            _host.InterruptCurrentMovement();
        }
    }

    public void HandleUpdateTarget(TargetInfo info)
    {
        if (info.ObjectId != TargetId)
            return;

        if (info.Status == TargetStatus.Ok)
        {
            Initialized = true;
            TargetPosition = info.TargetPosition;
            return;
        }

        if (TargetId != 0)
        {
            TargetId = 0;
            Initialized = false;
            _host.ClearTarget();
            _host.InterruptCurrentMovement();
        }
    }

    public void AdjustOffset(MotionDeltaFrame offset, double quantum)
    {
        if (TargetId == 0 || !Initialized)
            return;

        var self = _host.Position;
        var target = _host.GetRelationshipTarget(TargetId);
        var targetPos = target != null ? target.Position : TargetPosition;

        // offset = local-frame, Z-flattened vector from self to target.
        Vector3 worldOffset = targetPos.Frame.Origin - self.Frame.Origin;
        Vector3 local = MoveToMath.GlobalToLocalVec(self.Frame.Orientation, worldOffset);
        local.Z = 0f;
        offset.Origin = local;

        float dist = MoveToMath.CylinderDistanceNoZ(
            _host.Radius, self.Frame.Origin, TargetRadius, targetPos.Frame.Origin) - StickyRadius;

        if (MoveToMath.NormalizeCheckSmall(ref offset.Origin))
            offset.Origin = Vector3.Zero;

        float speed = 0f;
        float? maxSpeed = _host.MinterpMaxSpeed;
        if (maxSpeed.HasValue)
            speed = maxSpeed.Value * FollowSpeedFactor;
        if (speed < MoveToMath.Epsilon)
            speed = FallbackFollowSpeed;

        float delta = speed * (float)quantum;
        if (delta >= MathF.Abs(dist))
            delta = dist;
        else if (dist < 0f)
            delta = -delta;
        offset.Origin *= delta;

        // Bounded turn to face the target (relative heading this tick).
        float curHeading = MoveToMath.GetHeading(self.Frame.Orientation);
        float targetHeading = MoveToMath.PositionHeading(self.Frame.Origin, targetPos.Frame.Origin);
        float heading = targetHeading - curHeading;
        if (MathF.Abs(heading) < MoveToMath.Epsilon)
            heading = 0f;
        if (heading < -MoveToMath.Epsilon)
            heading += 360f;
        offset.SetHeading(heading);
    }
}
