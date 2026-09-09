using System;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class RemoteDeOverlapMechanismTests
{
    private readonly ITestOutputHelper _out;
    public RemoteDeOverlapMechanismTests(ITestOutputHelper output) => _out = output;

    private const uint Lb = 0xA9B40000u;
    private const uint Cell = Lb | 0x0001u;
    private const float R = 0.48f, H = 1.835f, StepUp = 0.60f, StepDown = 0.60f;
    private const float ContactDist = 2f * R;
    private const float GroundZ = R;                   // foot sphere resting on the flat (Z=0) terrain
    private const float StepPerTick = 0.03f;
    private const float SettleSlack = 2f * StepPerTick + 0.01f;

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

    private static void RegisterPlayerAt(PhysicsEngine e, uint id, Vector3 c)
        => e.ShadowObjects.Register(id, 0u, c, Quaternion.Identity, R,
            0f, 0f, Lb, ShadowCollisionType.Sphere, 0f, 1f, 0u,
            EntityCollisionFlags.IsPlayer | EntityCollisionFlags.IsCreature, isStatic: false);

    private static PhysicsBody GroundedBody(Vector3 pos) => new PhysicsBody
    {
        Position = pos,
        Orientation = Quaternion.Identity,
        State = PhysicsStateFlags.ReportCollisions,
        TransientState = TransientStateFlags.Contact | TransientStateFlags.OnWalkable
                         | TransientStateFlags.Active,
        Velocity = Vector3.Zero,
        ContactPlaneValid = true,
        ContactPlane = new System.Numerics.Plane(Vector3.UnitZ, 0f),
        GroundNormal = Vector3.UnitZ,
    };

    private Vector3 StepToward(PhysicsEngine engine, uint id, PhysicsBody body, ref uint cell,
                              Vector3 target)
        => StepToward(engine, id, body, ref cell, target, R, H);

    private Vector3 StepToward(PhysicsEngine engine, uint id, PhysicsBody body, ref uint cell,
                              Vector3 target, float radius, float height)
    {
        Vector3 pre = body.Position;
        Vector3 flatTarget = new Vector3(target.X, target.Y, pre.Z);
        Vector3 delta = flatTarget - pre;
        float dist = delta.Length();
        Vector3 post = dist <= StepPerTick ? flatTarget : pre + delta / dist * StepPerTick;

        var r = engine.ResolveWithTransition(pre, post, cell, radius, height, StepUp, StepDown,
            isOnGround: true, body: body,
            moverFlags: ObjectInfoState.EdgeSlide,
            movingEntityId: id);

        body.Position = r.Position;
        if (r.CellId != 0) cell = r.CellId;
        return body.Position;
    }

    [Fact]
    public void ConvergingCreatures_WithShadowFollowingResolved_SettleAtContactDistance()
    {
        var engine = BuildEngine();

        uint idA = 0xA1u, idB = 0xB2u;
        var target = new Vector3(10f, 10f, GroundZ);
        var a = GroundedBody(new Vector3(9f, 10f, GroundZ));
        var b = GroundedBody(new Vector3(11f, 10f, GroundZ));
        RegisterCreatureAt(engine, idA, a.Position);
        RegisterCreatureAt(engine, idB, b.Position);
        uint cellA = Cell, cellB = Cell;

        float sepAt = 0f;
        for (int tick = 0; tick < 250; tick++)
        {
            var pa = StepToward(engine, idA, a, ref cellA, target);
            engine.ShadowObjects.UpdatePosition(idA, pa, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellA);

            var pb = StepToward(engine, idB, b, ref cellB, target);
            engine.ShadowObjects.UpdatePosition(idB, pb, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellB);

            float s = Vector2.Distance(new(a.Position.X, a.Position.Y), new(b.Position.X, b.Position.Y));
            if (tick == 200) sepAt = s;  // separation snapshot 50 ticks before the end
            if (tick % 50 == 0 || tick == 249)
                _out.WriteLine($"tick{tick,3}: A=({a.Position.X:F2},{a.Position.Y:F2}) " +
                    $"B=({b.Position.X:F2},{b.Position.Y:F2}) sep={s:F3}");
        }

        float sep = Vector2.Distance(new(a.Position.X, a.Position.Y),
                                     new(b.Position.X, b.Position.Y));
        _out.WriteLine($"with-sync final sep={sep:F3} m (contact-distance = {ContactDist:F2} m)");
        Assert.True(sep >= ContactDist - 0.16f,
            $"converging creatures must de-overlap to near contact-distance; got {sep:F3} m (contact {ContactDist:F2})");
        Assert.True(MathF.Abs(sep - sepAt) < 0.02f,
            $"the de-overlapped separation must be stable; drifted {sepAt:F3} -> {sep:F3}");
    }

    [Fact]
    public void ConvergingCreatures_WithoutShadowSync_Overlap_ProvingTheSyncIsLoadBearing()
    {
        var engine = BuildEngine();

        uint idA = 0xA1u, idB = 0xB2u;
        var target = new Vector3(10f, 10f, GroundZ);
        var a = GroundedBody(new Vector3(9f, 10f, GroundZ));
        var b = GroundedBody(new Vector3(11f, 10f, GroundZ));
        RegisterCreatureAt(engine, idA, a.Position);
        RegisterCreatureAt(engine, idB, b.Position);
        uint cellA = Cell, cellB = Cell;

        for (int tick = 0; tick < 250; tick++)
        {
            StepToward(engine, idA, a, ref cellA, target);
            StepToward(engine, idB, b, ref cellB, target);
        }

        float sep = Vector2.Distance(new(a.Position.X, a.Position.Y),
                                     new(b.Position.X, b.Position.Y));
        _out.WriteLine($"no-sync final sep={sep:F3} m (contact-distance = {ContactDist:F2} m)");
        Assert.True(sep < 0.40f,
            $"without the shadow-follows-resolved sync the creatures should heavily OVERLAP (< 0.40 m — both reach " +
            $"the shared centre because each sweeps only its neighbour's stale start-shadow); got {sep:F3} m — " +
            $"if this fails the sync may not be the mechanism, rethink before wiring");
    }

    [Fact]
    public void ConvergingCreatures_RealInterpLoop_DeOverlapsAndAbsorbsTheStallBlip()
    {
        var engine = BuildEngine();
        uint idA = 0xA1u, idB = 0xB2u;
        var target = new Vector3(10f, 10f, GroundZ);
        var a = GroundedBody(new Vector3(9f, 10f, GroundZ));
        var b = GroundedBody(new Vector3(11f, 10f, GroundZ));
        RegisterCreatureAt(engine, idA, a.Position);
        RegisterCreatureAt(engine, idB, b.Position);
        uint cellA = Cell, cellB = Cell;

        var interpA = new InterpolationManager();
        var interpB = new InterpolationManager();
        var combA = new RemoteMotionCombiner();
        var combB = new RemoteMotionCombiner();
        const float maxSpeed = 4f;
        const float dt = 1f / 60f;
        const int upEvery = 10;
        float maxSpikeA = 0f, maxSpikeB = 0f;

        for (int tick = 0; tick < 600; tick++)
        {
            if (tick % upEvery == 0)
            {
                interpA.Enqueue(target, 0f, isMovingTo: false, currentBodyPosition: a.Position);
                interpB.Enqueue(target, 0f, isMovingTo: false, currentBodyPosition: b.Position);
            }

            var preA = a.Position;
            a.Position += combA.ComputeOffset(dt, a.Position, Vector3.Zero, a.Orientation, interpA, maxSpeed);
            var rA = engine.ResolveWithTransition(preA, a.Position, cellA, R, H, StepUp, StepDown,
                isOnGround: true, body: a, moverFlags: ObjectInfoState.EdgeSlide, movingEntityId: idA);
            a.Position = rA.Position; if (rA.CellId != 0) cellA = rA.CellId;
            engine.ShadowObjects.UpdatePosition(idA, a.Position, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellA);

            var preB = b.Position;
            b.Position += combB.ComputeOffset(dt, b.Position, Vector3.Zero, b.Orientation, interpB, maxSpeed);
            var rB = engine.ResolveWithTransition(preB, b.Position, cellB, R, H, StepUp, StepDown,
                isOnGround: true, body: b, moverFlags: ObjectInfoState.EdgeSlide, movingEntityId: idB);
            b.Position = rB.Position; if (rB.CellId != 0) cellB = rB.CellId;
            engine.ShadowObjects.UpdatePosition(idB, b.Position, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellB);

            // Ignore the initial approach (they start 2 m apart and legitimately move
            // ~0.13 m/tick); measure the per-tick net move only once they are near the
            // equilibrium where the stall-blip fires.
            if (tick > 120)
            {
                maxSpikeA = MathF.Max(maxSpikeA, Vector2.Distance(new(a.Position.X, a.Position.Y), new(preA.X, preA.Y)));
                maxSpikeB = MathF.Max(maxSpikeB, Vector2.Distance(new(b.Position.X, b.Position.Y), new(preB.X, preB.Y)));
            }
        }

        float sep = Vector2.Distance(new(a.Position.X, a.Position.Y), new(b.Position.X, b.Position.Y));
        _out.WriteLine($"real-interp: sep={sep:F3} m, maxSpike A={maxSpikeA:F3} B={maxSpikeB:F3}");
        Assert.True(sep >= ContactDist - 0.16f,
            $"real-loop converging creatures must de-overlap to near contact-distance; got {sep:F3} m");
        Assert.True(maxSpikeA < 0.30f && maxSpikeB < 0.30f,
            $"a stall-blip escaped the sweep (monster popped into its neighbour): " +
            $"maxSpike A={maxSpikeA:F3} B={maxSpikeB:F3} m (limit 0.30)");
    }

    [Fact]
    public void ConvergingLargeCreatures_DeOverlapWiderThanHuman()
    {
        const float bigR = 0.9f, bigH = 3.2f;
        const float bigContact = 2f * bigR;
        var engine = BuildEngine();
        uint idA = 0xA1u, idB = 0xB2u;
        var target = new Vector3(10f, 10f, bigR);
        var a = GroundedBody(new Vector3(7.5f, 10f, bigR));
        var b = GroundedBody(new Vector3(12.5f, 10f, bigR));
        engine.ShadowObjects.Register(idA, 0u, a.Position, Quaternion.Identity, bigR,
            0f, 0f, Lb, ShadowCollisionType.Sphere, 0f, 1f, 0u, EntityCollisionFlags.IsCreature, isStatic: false);
        engine.ShadowObjects.Register(idB, 0u, b.Position, Quaternion.Identity, bigR,
            0f, 0f, Lb, ShadowCollisionType.Sphere, 0f, 1f, 0u, EntityCollisionFlags.IsCreature, isStatic: false);
        uint cellA = Cell, cellB = Cell;

        for (int tick = 0; tick < 300; tick++)
        {
            var pa = StepToward(engine, idA, a, ref cellA, target, bigR, bigH);
            engine.ShadowObjects.UpdatePosition(idA, pa, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellA);
            var pb = StepToward(engine, idB, b, ref cellB, target, bigR, bigH);
            engine.ShadowObjects.UpdatePosition(idB, pb, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellB);
        }

        float sep = Vector2.Distance(new(a.Position.X, a.Position.Y), new(b.Position.X, b.Position.Y));
        _out.WriteLine($"large-creature sep={sep:F3} m (big contact {bigContact:F2}, human contact {ContactDist:F2})");
        Assert.True(sep >= bigContact - 0.30f,
            $"large creatures must de-overlap near their 2R contact ({bigContact:F2} m); got {sep:F3} m");
        Assert.True(sep > ContactDist + 0.4f,
            $"large creatures must spread materially WIDER than the human contact ({ContactDist:F2} m); got {sep:F3} m");
    }

    [Fact]
    public void ConvergingPlayers_WalkThroughEachOther_PerRetailPvpExemption()
    {
        var engine = BuildEngine();
        uint idA = 0x50000001u, idB = 0x50000002u;   // player guids (0x50…)
        var target = new Vector3(10f, 10f, GroundZ);
        var a = GroundedBody(new Vector3(9f, 10f, GroundZ));
        var b = GroundedBody(new Vector3(11f, 10f, GroundZ));
        RegisterPlayerAt(engine, idA, a.Position);
        RegisterPlayerAt(engine, idB, b.Position);
        uint cellA = Cell, cellB = Cell;

        var interpA = new InterpolationManager();
        var interpB = new InterpolationManager();
        var combA = new RemoteMotionCombiner();
        var combB = new RemoteMotionCombiner();
        const float maxSpeed = 4f;
        const float dt = 1f / 60f;
        const int upEvery = 10;
        // Production remote-PLAYER mover flags (RemotePhysicsUpdater sweep, IsPlayerGuid branch).
        const ObjectInfoState playerMover = ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide;

        for (int tick = 0; tick < 600; tick++)
        {
            if (tick % upEvery == 0)
            {
                interpA.Enqueue(target, 0f, isMovingTo: false, currentBodyPosition: a.Position);
                interpB.Enqueue(target, 0f, isMovingTo: false, currentBodyPosition: b.Position);
            }

            var preA = a.Position;
            a.Position += combA.ComputeOffset(dt, a.Position, Vector3.Zero, a.Orientation, interpA, maxSpeed);
            var rA = engine.ResolveWithTransition(preA, a.Position, cellA, R, H, StepUp, StepDown,
                isOnGround: true, body: a, moverFlags: playerMover, movingEntityId: idA);
            a.Position = rA.Position; if (rA.CellId != 0) cellA = rA.CellId;
            engine.ShadowObjects.UpdatePosition(idA, a.Position, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellA);

            var preB = b.Position;
            b.Position += combB.ComputeOffset(dt, b.Position, Vector3.Zero, b.Orientation, interpB, maxSpeed);
            var rB = engine.ResolveWithTransition(preB, b.Position, cellB, R, H, StepUp, StepDown,
                isOnGround: true, body: b, moverFlags: playerMover, movingEntityId: idB);
            b.Position = rB.Position; if (rB.CellId != 0) cellB = rB.CellId;
            engine.ShadowObjects.UpdatePosition(idB, b.Position, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellB);
        }

        float sep = Vector2.Distance(new(a.Position.X, a.Position.Y), new(b.Position.X, b.Position.Y));
        _out.WriteLine($"player-vs-player (PvP exempt): sep={sep:F3} m (contact {ContactDist:F2})");
        Assert.True(sep < 0.40f,
            $"two non-PK player remotes must WALK THROUGH each other (retail PvP exemption); " +
            $"got sep={sep:F3} m — if this is near contact-distance the remote mover is missing IsPlayer");
    }

    [Fact]
    public void PlayerVsMonster_DeOverlapsAndAbsorbsTheStallBlip()
    {
        var engine = BuildEngine();
        uint idPlayer = 0x50000001u, idMonster = 0x80000002u;
        var target = new Vector3(10f, 10f, GroundZ);
        var p = GroundedBody(new Vector3(9f, 10f, GroundZ));   // player
        var m = GroundedBody(new Vector3(11f, 10f, GroundZ));  // monster
        RegisterPlayerAt(engine, idPlayer, p.Position);
        RegisterCreatureAt(engine, idMonster, m.Position);
        uint cellP = Cell, cellM = Cell;

        var interpP = new InterpolationManager();
        var interpM = new InterpolationManager();
        var combP = new RemoteMotionCombiner();
        var combM = new RemoteMotionCombiner();
        const float maxSpeed = 4f;
        const float dt = 1f / 60f;
        const int upEvery = 10;
        const ObjectInfoState playerMover = ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide;
        float maxSpikeP = 0f, maxSpikeM = 0f;

        for (int tick = 0; tick < 600; tick++)
        {
            if (tick % upEvery == 0)
            {
                interpP.Enqueue(target, 0f, isMovingTo: false, currentBodyPosition: p.Position);
                interpM.Enqueue(target, 0f, isMovingTo: false, currentBodyPosition: m.Position);
            }

            var preP = p.Position;
            p.Position += combP.ComputeOffset(dt, p.Position, Vector3.Zero, p.Orientation, interpP, maxSpeed);
            var rP = engine.ResolveWithTransition(preP, p.Position, cellP, R, H, StepUp, StepDown,
                isOnGround: true, body: p, moverFlags: playerMover, movingEntityId: idPlayer);
            p.Position = rP.Position; if (rP.CellId != 0) cellP = rP.CellId;
            engine.ShadowObjects.UpdatePosition(idPlayer, p.Position, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellP);

            var preM = m.Position;
            m.Position += combM.ComputeOffset(dt, m.Position, Vector3.Zero, m.Orientation, interpM, maxSpeed);
            var rM = engine.ResolveWithTransition(preM, m.Position, cellM, R, H, StepUp, StepDown,
                isOnGround: true, body: m, moverFlags: ObjectInfoState.EdgeSlide, movingEntityId: idMonster);
            m.Position = rM.Position; if (rM.CellId != 0) cellM = rM.CellId;
            engine.ShadowObjects.UpdatePosition(idMonster, m.Position, Quaternion.Identity, 0f, 0f, Lb, seedCellId: cellM);

            if (tick > 120)
            {
                maxSpikeP = MathF.Max(maxSpikeP, Vector2.Distance(new(p.Position.X, p.Position.Y), new(preP.X, preP.Y)));
                maxSpikeM = MathF.Max(maxSpikeM, Vector2.Distance(new(m.Position.X, m.Position.Y), new(preM.X, preM.Y)));
            }
        }

        float sep = Vector2.Distance(new(p.Position.X, p.Position.Y), new(m.Position.X, m.Position.Y));
        _out.WriteLine($"player-vs-monster: sep={sep:F3} m, maxSpike P={maxSpikeP:F3} M={maxSpikeM:F3}");
        Assert.True(sep >= ContactDist - 0.16f,
            $"a player converging on a monster must de-overlap to near contact-distance (no PvP exemption vs a creature); got {sep:F3} m");
        Assert.True(maxSpikeP < 0.30f && maxSpikeM < 0.30f,
            $"Movement stalling reintroduced for the player mover — a stall-blip escaped the sweep: maxSpike P={maxSpikeP:F3} M={maxSpikeM:F3} m (limit 0.30)");
    }
}
