using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class CrowdJumpTests
{
    private readonly ITestOutputHelper _out;
    public CrowdJumpTests(ITestOutputHelper output) => _out = output;

    private const uint Lb = 0xA9B40000u;
    private const uint Cell = Lb | 0x0001u;
    private const float R = 0.48f, H = 1.835f, StepUp = 0.60f, StepDown = 0.04f;
    private const float Dt = 1f / 30f;   // one physics quantum

    private static PhysicsEngine BuildEngine()
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(Lb, new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return engine;
    }

    private static void RegisterCreatureAt(PhysicsEngine e, uint id, Vector3 c)
        => e.ShadowObjects.Register(id, 0u, c, Quaternion.Identity, R,
            0f, 0f, Lb, ShadowCollisionType.Sphere, 0f, 1f, 0u,
            EntityCollisionFlags.IsCreature, isStatic: false);

    [Fact]
    public void BlockedJump_VelocityBleedsToZero_ThenGravityResumes()
    {
        var engine = BuildEngine();
        RegisterCreatureAt(engine, 0xC0B0u, new Vector3(12f, 10f, 4.5f));

        var body = new PhysicsBody
        {
            Position = new Vector3(12f, 10f, 3.0f),
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            TransientState = TransientStateFlags.None,     // airborne
            Velocity = new Vector3(0f, 0f, 12f),           // a jump: moving straight up
        };
        uint cell = Cell;

        bool bled = false;
        for (int frame = 0; frame < 20; frame++)
        {
            var pre = body.Position;
            body.calc_acceleration();
            body.UpdatePhysicsInternal(Dt);
            var post = body.Position;
            bool candidateMoved = post != pre;

            var r = engine.ResolveWithTransition(pre, post, cell, R, H, StepUp, StepDown,
                isOnGround: false, body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);

            bool prevOnWalkable = body.OnWalkable;
            body.Position = r.Position;
            cell = r.CellId;
            if (r.IsOnGround && body.Velocity.Z <= 0f)
            {
                body.TransientState |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
                body.calc_acceleration();
                if (body.Velocity.Z < 0f) body.Velocity = new Vector3(body.Velocity.X, body.Velocity.Y, 0f);
            }
            else
            {
                body.TransientState &= ~(TransientStateFlags.Contact | TransientStateFlags.OnWalkable);
                body.calc_acceleration();
            }

            if (candidateMoved)
                PhysicsObjUpdate.HandleAllCollisions(body,
                    r.CollisionNormalValid, r.CollisionNormal,
                    prevContact: false, prevOnWalkable, nowOnWalkable: body.OnWalkable);

            _out.WriteLine($"frame{frame,2}: z={body.Position.Z:F3} vz={body.Velocity.Z:F3} " +
                $"fsf={body.FramesStationaryFall}");

            if (frame >= 1 && frame <= 6 && body.Velocity.Z <= 0.5f)
                bled = true;
        }

        Assert.True(bled, "the blocked jump velocity must bleed to ~0 within a few frames (fsf>1 → v=0)");
        // After bleeding, gravity has taken over — the body is no longer being shoved upward.
        Assert.True(body.Velocity.Z <= 0.5f, $"velocity should not persist upward; got vz={body.Velocity.Z:F3}");
    }
}
