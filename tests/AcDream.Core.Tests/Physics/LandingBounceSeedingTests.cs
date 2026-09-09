using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class LandingBounceSeedingTests
{
    private const uint Lb = 0x00010000u;
    private const uint Cell = 0x0001u;

    private static PhysicsEngine BuildFlatEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(Lb, new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return engine;
    }

    private static PhysicsBody GroundedBody(Vector3 pos, Vector3 velocity) => new()
    {
        Position = pos,
        Orientation = Quaternion.Identity,
        State = PhysicsStateFlags.Gravity,
        TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        Velocity = velocity,
        ContactPlaneValid = true,
        ContactPlane = new Plane(Vector3.UnitZ, 0f),
        GroundNormal = Vector3.UnitZ,
    };

    private static ResolveResult ZeroMoveResolve(PhysicsEngine engine, PhysicsBody body)
        => engine.ResolveWithTransition(
            body.Position, body.Position, Cell,
            sphereRadius: 0.48f, sphereHeight: 1.835f,
            stepUpHeight: 0.55f, stepDownHeight: 0.55f,
            isOnGround: body.OnWalkable,
            body: body);

    [Fact]
    public void CheckContact_AtRestGroundedBody_KeepsContactOnZeroMoveResolve()
    {
        var engine = BuildFlatEngine();
        var body = GroundedBody(new Vector3(96f, 96f, 0.48f), Vector3.Zero);

        var result = ZeroMoveResolve(engine, body);

        Assert.True(result.IsOnGround);
        Assert.True(result.InContact);
    }

    [Fact]
    public void CheckContact_AscendingJumper_SeedsNoContact()
    {
        var engine = BuildFlatEngine();
        var body = GroundedBody(new Vector3(96f, 96f, 0.48f), new Vector3(0f, 0f, 5.4f));

        var result = ZeroMoveResolve(engine, body);

        Assert.False(result.IsOnGround);
        Assert.False(result.InContact);
    }

    [Fact]
    public void CheckContact_ContactWithoutStoredPlane_SeedsNothing()
    {
        var engine = BuildFlatEngine();
        var body = GroundedBody(new Vector3(96f, 96f, 0.48f), Vector3.Zero);
        body.ContactPlaneValid = false;

        var result = ZeroMoveResolve(engine, body);

        Assert.False(result.IsOnGround);
    }

    [Fact]
    public void Reflect_DownhillSlopeLanding_FivePercentNormalReversal_TangentialKept()
    {
        var n = Vector3.Normalize(new Vector3(0f, 0.5f, 0.8660254f));
        var v = new Vector3(0f, 4f, -6f);
        var body = new PhysicsBody
        {
            Velocity = v,
            Elasticity = 0.05f,
            FramesStationaryFall = 0,
        };

        PhysicsObjUpdate.HandleAllCollisions(
            body,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: false, prevOnWalkable: false, nowOnWalkable: true);

        float dotBefore = Vector3.Dot(v, n);
        float dotAfter = Vector3.Dot(body.Velocity, n);
        Assert.True(dotBefore < 0f);
        Assert.True(MathF.Abs(dotAfter - (-dotBefore * 0.05f)) < 1e-5f,
            $"normal component: before={dotBefore}, after={dotAfter}");
        Vector3 tangBefore = v - n * dotBefore;
        Vector3 tangAfter = body.Velocity - n * dotAfter;
        Assert.True((tangAfter - tangBefore).Length() < 1e-5f);
    }

    [Fact]
    public void Reflect_SleddingOverridesGroundedSuppression()
    {
        var n = Vector3.UnitZ;
        var body = new PhysicsBody
        {
            Velocity = new Vector3(3f, 0f, -2f),
            Elasticity = 0.05f,
            State = PhysicsStateFlags.Sledding,
            FramesStationaryFall = 0,
        };

        PhysicsObjUpdate.HandleAllCollisions(
            body,
            collisionNormalValid: true, collisionNormal: n,
            prevContact: true, prevOnWalkable: true, nowOnWalkable: true);

        Assert.True(MathF.Abs(body.Velocity.Z - 0.1f) < 1e-5f,
            $"expected reflected +0.1 (=2·0.05), got {body.Velocity.Z}");
        Assert.Equal(3f, body.Velocity.X, precision: 5);
    }
}
