using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class HandleAllCollisionsTests
{
    private static PhysicsBody Airborne(Vector3 v, int fsf = 0) => new PhysicsBody
    {
        State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
        TransientState = TransientStateFlags.None,
        Velocity = v,
        FramesStationaryFall = fsf,
    };

    [Fact]
    public void SetPositionContact_SteepSurfaceIsContactButNotWalkable()
    {
        var body = Airborne(new Vector3(0f, 0f, -1f));
        Vector3 steepNormal = Vector3.Normalize(new Vector3(1f, 0f, 0.5f));
        bool onWalkable = PhysicsObjUpdate.IsWalkableContact(
            inContact: true,
            contactNormal: steepNormal);

        PhysicsObjUpdate.ApplySetPositionContact(
            body,
            inContact: true,
            onWalkable: onWalkable);

        Assert.True(body.InContact);
        Assert.False(body.OnWalkable);
        Assert.Equal(PhysicsBody.Gravity, body.Acceleration.Z);
    }

    [Fact]
    public void SetPositionContact_WalkableSurfaceSetsBothFacts()
    {
        var body = Airborne(new Vector3(0f, 0f, -1f));
        bool onWalkable = PhysicsObjUpdate.IsWalkableContact(
            inContact: true,
            contactNormal: new Vector3(0f, 0f, PhysicsGlobals.FloorZ));

        PhysicsObjUpdate.ApplySetPositionContact(
            body,
            inContact: true,
            onWalkable: onWalkable);

        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.Equal(Vector3.Zero, body.Acceleration);
    }

    [Fact]
    public void Fsf0_AirborneWallHit_ReflectsIntoWallComponent()
    {
        var b = Airborne(new Vector3(3f, 0f, 0f));   // moving +X into a wall whose outward normal is -X
        var n = new Vector3(-1f, 0f, 0f);
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: false);
        // dot = 3*-1 = -3 < 0 → k = -(-3*(0.05+1)) = 3.15 → v += (-1,0,0)*3.15 → x = 3 - 3.15 = -0.15
        Assert.Equal(-0.15f, b.Velocity.X, precision: 3);
    }

    [Fact]
    public void Fsf0_MovingAwayFromSurface_DoesNotReflect()
    {
        var b = Airborne(new Vector3(0f, 0f, 2f));   // moving up, normal also up (already separating)
        var n = new Vector3(0f, 0f, 1f);
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: false);
        Assert.Equal(2f, b.Velocity.Z);   // dot = +2 >= 0 → no reflection
    }

    [Fact]
    public void Fsf2_ZeroesVelocity_TheAirborneStuckBleed()
    {
        var b = Airborne(new Vector3(0f, 0f, 18f), fsf: 2);
        var n = new Vector3(-0.96f, -0.25f, -0.15f);
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: false);
        Assert.Equal(Vector3.Zero, b.Velocity);
    }

    [Fact]
    public void Fsf3_ZeroesVelocity_EvenWithoutCollisionNormal()
    {
        var b = Airborne(new Vector3(1f, 2f, 18f), fsf: 3);
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: false, collisionNormal: default,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: false);
        Assert.Equal(Vector3.Zero, b.Velocity);
    }

    [Fact]
    public void StayingOnWalkable_DoesNotReflect_CorridorWallSlidePreserved()
    {
        var b = new PhysicsBody
        {
            Velocity = new Vector3(3f, 0f, 0f),
            TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
            FramesStationaryFall = 0,
        };
        var n = new Vector3(-1f, 0f, 0f);
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: true, prevOnWalkable: true, nowOnWalkable: true);
        Assert.Equal(3f, b.Velocity.X);
    }

    [Fact]
    public void Inelastic_ZeroesInsteadOfReflecting()
    {
        var b = Airborne(new Vector3(3f, 0f, 0f));
        b.State |= PhysicsStateFlags.Inelastic;   // spell projectile / missile
        var n = new Vector3(-1f, 0f, 0f);
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: false);
        Assert.Equal(Vector3.Zero, b.Velocity);
    }

    [Fact]
    public void LandingReflects_RetailRuleRestored_NotSuppressed()
    {
        var b = Airborne(new Vector3(0f, 0f, -5f));   // falling
        var n = new Vector3(0f, 0f, 1f);              // floor normal up
        PhysicsObjUpdate.HandleAllCollisions(b,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: true);
        // dot = -5 < 0 → k = -(-5*1.05) = 5.25 → v.z = -5 + 5.25 = 0.25 (tiny bounce)
        Assert.Equal(0.25f, b.Velocity.Z, precision: 3);
    }

    [Fact]
    public void SetPositionTransition_HiddenLandingCallbackRunsBeforeCollisionResponse()
    {
        var body = Airborne(Vector3.Zero);
        int hitGroundCalls = 0;

        PhysicsObjUpdate.CommitSetPositionTransition(
            body,
            inContact: true,
            onWalkable: true,
            collisionNormalValid: true,
            collisionNormal: Vector3.UnitZ,
            previousContact: false,
            previousOnWalkable: false,
            hitGround: () =>
            {
                hitGroundCalls++;
                body.Velocity = new Vector3(0f, 0f, -4f);
            });

        Assert.Equal(1, hitGroundCalls);
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);
        Assert.Equal(0.2f, body.Velocity.Z, precision: 3);
    }

    [Fact]
    public void SetPositionTransition_HiddenWalkOffCallbackRunsBeforeCollisionResponse()
    {
        var body = Airborne(Vector3.Zero);
        body.TransientState = TransientStateFlags.Contact
                            | TransientStateFlags.OnWalkable;
        int leaveGroundCalls = 0;

        PhysicsObjUpdate.CommitSetPositionTransition(
            body,
            inContact: false,
            onWalkable: false,
            collisionNormalValid: true,
            collisionNormal: -Vector3.UnitX,
            previousContact: true,
            previousOnWalkable: true,
            leaveGround: () =>
            {
                leaveGroundCalls++;
                body.Velocity = new Vector3(3f, 0f, 0f);
            });

        Assert.Equal(1, leaveGroundCalls);
        Assert.False(body.InContact);
        Assert.False(body.OnWalkable);
        Assert.Equal(-0.15f, body.Velocity.X, precision: 3);
    }
}
