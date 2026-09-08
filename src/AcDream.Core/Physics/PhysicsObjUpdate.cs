using System.Numerics;

namespace AcDream.Core.Physics;

public static class PhysicsObjUpdate
{
    public static bool IsWalkableContact(bool inContact, Vector3 contactNormal)
        => inContact && contactNormal.Z >= PhysicsGlobals.FloorZ;

    public static void ApplySetPositionContact(
        PhysicsBody body,
        bool inContact,
        bool onWalkable)
    {
        if (inContact)
            body.TransientState |= TransientStateFlags.Contact;
        else
            body.TransientState &= ~TransientStateFlags.Contact;

        if (inContact && onWalkable)
            body.TransientState |= TransientStateFlags.OnWalkable;
        else
            body.TransientState &= ~TransientStateFlags.OnWalkable;

        if (body.ContactPlaneIsWater)
            body.TransientState |= TransientStateFlags.WaterContact;
        else
            body.TransientState &= ~TransientStateFlags.WaterContact;

        body.calc_acceleration();
    }

    public static bool CommitSetPositionTransition(
        PhysicsBody body,
        bool inContact,
        bool onWalkable,
        bool collisionNormalValid,
        Vector3 collisionNormal,
        bool previousContact,
        bool previousOnWalkable,
        Action? hitGround = null,
        Action? leaveGround = null,
        Func<bool>? isCurrent = null,
        Func<bool>? isVelocityCurrent = null)
    {
        if (!CommitSetPositionContactTransition(
                body,
                inContact,
                onWalkable,
                previousOnWalkable,
                hitGround,
                leaveGround,
                isCurrent))
        {
            return false;
        }

        if (isVelocityCurrent?.Invoke() == false)
            return isCurrent?.Invoke() ?? true;

        HandleAllCollisions(
            body,
            collisionNormalValid,
            collisionNormal,
            previousContact,
            previousOnWalkable,
            body.OnWalkable);
        return isCurrent?.Invoke() ?? true;
    }

    public static bool CommitSetPositionContactTransition(
        PhysicsBody body,
        bool inContact,
        bool onWalkable,
        bool previousOnWalkable,
        Action? hitGround = null,
        Action? leaveGround = null,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        bool finalOnWalkable = CommitSetPositionContactPrefix(
            body,
            inContact,
            onWalkable,
            previousOnWalkable);

        if (!previousOnWalkable && finalOnWalkable)
        {
            hitGround?.Invoke();
            if (isCurrent?.Invoke() == false)
                return false;
        }
        else if (previousOnWalkable && !finalOnWalkable)
        {
            leaveGround?.Invoke();
            if (isCurrent?.Invoke() == false)
                return false;
        }
        CommitSetPositionPostGround(body);
        return isCurrent?.Invoke() ?? true;
    }

    public static bool CommitSetPositionContactPrefix(
        PhysicsBody body,
        bool inContact,
        bool onWalkable,
        bool previousOnWalkable)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (previousOnWalkable)
            body.TransientState |= TransientStateFlags.OnWalkable;
        else
            body.TransientState &= ~TransientStateFlags.OnWalkable;
        if (inContact)
            body.TransientState |= TransientStateFlags.Contact;
        else
            body.TransientState &= ~TransientStateFlags.Contact;
        body.calc_acceleration();
        bool finalOnWalkable = inContact && onWalkable;
        if (finalOnWalkable)
            body.TransientState |= TransientStateFlags.OnWalkable;
        else
            body.TransientState &= ~TransientStateFlags.OnWalkable;
        if (body.ContactPlaneIsWater)
            body.TransientState |= TransientStateFlags.WaterContact;
        else
            body.TransientState &= ~TransientStateFlags.WaterContact;
        return finalOnWalkable;
    }

    public static void CommitSetPositionPostGround(PhysicsBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        body.calc_acceleration();
    }

    public static void HandleAllCollisions(
        PhysicsBody body,
        bool collisionNormalValid, Vector3 collisionNormal,
        bool prevContact, bool prevOnWalkable, bool nowOnWalkable)
    {
        bool sledding = (body.State & PhysicsStateFlags.Sledding) != 0;
        bool shouldReflect = !(prevOnWalkable && nowOnWalkable && !sledding);

        if (body.FramesStationaryFall <= 1)
        {
            if (shouldReflect && collisionNormalValid)
            {
                if ((body.State & PhysicsStateFlags.Inelastic) != 0)
                {
                    body.Velocity = Vector3.Zero;                          // pc:282720-282722
                }
                else
                {
                    float dot = Vector3.Dot(body.Velocity, collisionNormal);
                    if (dot < 0f)                                          // moving INTO the surface
                    {
                        float k = -(dot * (body.Elasticity + 1f));         // pc:282712
                        body.Velocity += collisionNormal * k;
                    }
                }
            }
        }
        else
        {
            body.Velocity = Vector3.Zero;                                  // fsf>1 → THE BLEED (pc:282729)
        }

        _ = prevContact;
    }
}
