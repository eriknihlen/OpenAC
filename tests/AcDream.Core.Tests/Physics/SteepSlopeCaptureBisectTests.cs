using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class SteepSlopeCaptureBisectTests
{
    private readonly ITestOutputHelper _out;
    public SteepSlopeCaptureBisectTests(ITestOutputHelper output) => _out = output;

    private static readonly Vector3 RoofV0 = new(240f, 0f, 88f);
    private static readonly Vector3 RoofV1 = new(264f, 0f, 80f);
    private static readonly Vector3 RoofV2 = new(264f, 24f, 68f);

    private const uint CellId = 0x00000001u;
    private const uint LandblockId = 0x00000000u;
    private const uint SyntheticGfxId = 0x265BEEF1u;

    private const int TicksPerSecond = 30;
    private const float Gravity = -9.8f;
    private const float SphereRadius = 0.48f;
    private const float SphereHeight = 1.835f;

    private static readonly Vector3 ApproachStartPosReal = new(244.59f, -0.81f, 92.79f);
    private static readonly Vector3 ApproachStartVel = new(11.1509495f, 14.129979f, -16.72f);

    private static readonly Vector3 RoofCentroid = (RoofV0 + RoofV1 + RoofV2) / 3f;

    private static PhysicsEngine MakeRoofEngine(float scale = 1f)
    {
        float boundingRadius = 30f * MathF.Max(scale, 1f);
        var resolved = new Dictionary<ushort, ResolvedPolygon>();
        var verts = new[]
        {
            (RoofV0 - RoofCentroid) * scale,
            (RoofV1 - RoofCentroid) * scale,
            (RoofV2 - RoofCentroid) * scale,
        };
        var normal = Vector3.Normalize(Vector3.Cross(verts[1] - verts[0], verts[2] - verts[0]));
        float d = -Vector3.Dot(normal, verts[0]);
        resolved[1] = new ResolvedPolygon
        {
            Vertices = verts,
            Plane = new Plane(normal, d),
            NumPoints = 3,
            SidesType = CullMode.None,
        };

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = boundingRadius },
        };
        leaf.Polygons.Add(1);

        var heights = new byte[81];
        var heightTab = new float[256];
        for (int i = 0; i < 256; i++) heightTab[i] = -1000f; // terrain never interferes

        var engine = new PhysicsEngine();
        engine.AddLandblock(
            LandblockId,
            new TerrainSurface(heights, heightTab),
            System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(),
            worldOffsetX: 0f, worldOffsetY: 0f);

        var cache = new PhysicsDataCache();
        var bspTree = new PhysicsBSPTree { Root = leaf };
        var physics = new GfxObjPhysics
        {
            BSP = bspTree,
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices = new VertexArray(),
            Resolved = resolved,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = boundingRadius },
        };
        cache.RegisterGfxObjForTest(SyntheticGfxId, physics);
        engine.DataCache = cache;

        engine.ShadowObjects.Register(
            entityId: SyntheticGfxId,
            gfxObjId: SyntheticGfxId,
            worldPos: Vector3.Zero,
            rotation: Quaternion.Identity,
            radius: boundingRadius,
            worldOffsetX: 0f,
            worldOffsetY: 0f,
            landblockId: LandblockId,
            collisionType: ShadowCollisionType.BSP,
            scale: 1.0f,
            seedCellId: CellId);

        return engine;
    }

    public sealed record TickSample(
        int Tick,
        Vector3 Pos,
        float Advance,
        bool CollisionNormalValid,
        Vector3 CollisionNormal,
        bool OnGround,
        int FrozenStreak);

    public static List<TickSample> ReplayRealRoofLanding(int postLandingTicks = 60)
    {
        var engine = MakeRoofEngine();
        const float dt = 1f / TicksPerSecond;

        var body = new PhysicsBody { TransientState = TransientStateFlags.Active };

        Vector3 pos = ApproachStartPosReal - RoofCentroid;
        Vector3 vel = ApproachStartVel;
        uint cell = CellId;
        bool grounded = false;
        int frozenStreak = 0;
        int ticksSinceGrounded = -1;

        var samples = new List<TickSample>();

        int maxTicks = 18 + postLandingTicks + 20;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            if (!grounded)
                vel = new Vector3(vel.X, vel.Y, vel.Z + Gravity * dt);
            Vector3 requestedVel = grounded ? new Vector3(vel.X, vel.Y, 0f) : vel;
            Vector3 target = pos + requestedVel * dt;

            var result = engine.ResolveWithTransition(
                currentPos: pos,
                targetPos: target,
                cellId: cell,
                sphereRadius: SphereRadius,
                sphereHeight: SphereHeight,
                stepUpHeight: 0.6f,
                stepDownHeight: 1.5f,
                isOnGround: grounded,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000000u);

            float advance = Vector3.Distance(result.Position, pos);
            if (advance < 0.001f)
                frozenStreak++;
            else
                frozenStreak = 0;

            samples.Add(new TickSample(
                tick, result.Position, advance,
                result.CollisionNormalValid, result.CollisionNormal,
                result.IsOnGround, frozenStreak));

            pos = result.Position;
            cell = result.CellId;
            body.Position = pos;

            if (!grounded && result.IsOnGround)
            {
                grounded = true;
                ticksSinceGrounded = 0;
            }
            else if (grounded)
            {
                ticksSinceGrounded++;
                if (ticksSinceGrounded >= postLandingTicks)
                    break;
            }
        }

        return samples;
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void RealCapturedRoofLanding_CharacterizeCurrentBehavior()
    {
        PhysicsDiagnostics.ResetForTest();
        PhysicsDiagnostics.ProbeIndoorBspEnabled = true;
        PhysicsDiagnostics.ProbeBuildingEnabled = true;
        try
        {
            var samples = ReplayRealRoofLanding();

            int maxFrozen = 0;
            int landedAtTick = -1;
            foreach (var s in samples)
            {
                maxFrozen = System.Math.Max(maxFrozen, s.FrozenStreak);
                if (landedAtTick < 0 && s.OnGround) landedAtTick = s.Tick;
                _out.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "t{0,3}: pos=({1:F3},{2:F3},{3:F3}) adv={4:F4} cnv={5} n=({6:F3},{7:F3},{8:F3}) onGround={9} frozen={10}",
                    s.Tick, s.Pos.X, s.Pos.Y, s.Pos.Z, s.Advance,
                    s.CollisionNormalValid, s.CollisionNormal.X, s.CollisionNormal.Y, s.CollisionNormal.Z,
                    s.OnGround, s.FrozenStreak));
            }

            _out.WriteLine($"=== landedAtTick={landedAtTick} maxFrozenStreak={maxFrozen} totalTicks={samples.Count} ===");

            // Sanity-only assertion: the replay must actually reach the roof
            // (land) within the ballistic approach window — if this fails the
            // synthetic fixture itself is wrong, not a physics-engine finding.
            Assert.True(landedAtTick is >= 0 and < 30,
                $"Replay never reached the synthetic roof polygon (landedAtTick={landedAtTick}); " +
                "fixture geometry or approach trajectory needs adjustment before this is a valid oracle.");
        }
        finally
        {
            PhysicsDiagnostics.ResetForTest();
        }
    }


    public sealed record ComposedTickSample(
        int Tick,
        Vector3 Pos,
        Vector3 Velocity,
        float Advance,
        bool CollisionNormalValid,
        Vector3 CollisionNormal,
        bool OnWalkable,
        int FrozenStreak);

    public static List<ComposedTickSample> ReplayRealRoofLandingComposed(
        bool preserveResidualVelocityOnGroundedTick,
        int postLandingTicks = 90)
    {
        var engine = MakeRoofEngine(scale: 6f);
        const float dt = 1f / TicksPerSecond;

        var body = new PhysicsBody { TransientState = TransientStateFlags.Active };
        body.Position = ApproachStartPosReal - RoofCentroid;
        body.Velocity = ApproachStartVel;
        uint cell = CellId;
        int frozenStreak = 0;
        int ticksSinceGrounded = -1;

        var samples = new List<ComposedTickSample>();
        int maxTicks = 18 + postLandingTicks + 20;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            if (body.OnWalkable && !preserveResidualVelocityOnGroundedTick)
            {
                float savedVz = body.Velocity.Z;
                body.Velocity = new Vector3(0f, 0f, savedVz);
            }

            Vector3 preIntegratePos = body.Position;
            bool onGroundBeforeResolve = body.OnWalkable;

            body.calc_acceleration();
            body.UpdatePhysicsInternal(dt);

            Vector3 postIntegratePos = body.Position;
            bool candidateMoved = postIntegratePos != preIntegratePos;

            var result = engine.ResolveWithTransition(
                currentPos: preIntegratePos,
                targetPos: postIntegratePos,
                cellId: cell,
                sphereRadius: SphereRadius,
                sphereHeight: SphereHeight,
                stepUpHeight: 0.6f,
                stepDownHeight: 1.5f,
                isOnGround: onGroundBeforeResolve,
                body: body,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000000u);

            float advance = Vector3.Distance(result.Position, preIntegratePos);
            if (advance < 0.001f) frozenStreak++; else frozenStreak = 0;

            bool prevContact = body.InContact;
            bool prevOnWalkable = body.OnWalkable;

            body.Position = result.Position;
            cell = result.CellId;

            if (result.IsOnGround && body.Velocity.Z <= 0f)
            {
                body.TransientState |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
                body.calc_acceleration();
                if (body.Velocity.Z < 0f)
                    body.Velocity = new Vector3(body.Velocity.X, body.Velocity.Y, 0f);
            }
            else
            {
                body.TransientState &= ~(TransientStateFlags.Contact | TransientStateFlags.OnWalkable);
                body.calc_acceleration();
            }

            if (candidateMoved)
            {
                PhysicsObjUpdate.HandleAllCollisions(
                    body,
                    result.CollisionNormalValid, result.CollisionNormal,
                    prevContact, prevOnWalkable, nowOnWalkable: body.OnWalkable);
            }

            samples.Add(new ComposedTickSample(
                tick, body.Position, body.Velocity, advance,
                result.CollisionNormalValid, result.CollisionNormal,
                body.OnWalkable, frozenStreak));

            if (!onGroundBeforeResolve && body.OnWalkable)
            {
                ticksSinceGrounded = 0;
            }
            else if (body.OnWalkable)
            {
                ticksSinceGrounded++;
                if (ticksSinceGrounded >= postLandingTicks)
                    break;
            }
        }

        return samples;
    }

    private void DumpComposed(string label, List<ComposedTickSample> samples)
    {
        _out.WriteLine($"=== {label} ===");
        foreach (var s in samples)
        {
            _out.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "t{0,3}: pos=({1:F3},{2:F3},{3:F3}) vel=({4:F3},{5:F3},{6:F3}) adv={7:F4} " +
                "cnv={8} n=({9:F3},{10:F3},{11:F3}) onWalk={12} frozen={13}",
                s.Tick, s.Pos.X, s.Pos.Y, s.Pos.Z,
                s.Velocity.X, s.Velocity.Y, s.Velocity.Z, s.Advance,
                s.CollisionNormalValid, s.CollisionNormal.X, s.CollisionNormal.Y, s.CollisionNormal.Z,
                s.OnWalkable, s.FrozenStreak));
        }
    }

    [Fact]
    public void ComposedRoofLanding_OldZeroingModel_ReproducesTheMinedFreeze()
    {
        PhysicsDiagnostics.ResetForTest();
        try
        {
            var samples = ReplayRealRoofLandingComposed(
                preserveResidualVelocityOnGroundedTick: false);
            DumpComposed("OLD (zero horizontal velocity every grounded tick)", samples);

            int landedAtTick = samples.FindIndex(s => s.OnWalkable);
            Assert.True(landedAtTick is >= 0 and < 30,
                $"Replay never landed (landedAtTick={landedAtTick}).");

            // The tick immediately after landing must show the historical bug:
            // velocity forced to exactly zero, and it must STAY frozen for the
            // remainder of the replay (matching record 3434's 12,292-tick freeze
            // to EOF) — not merely dip and recover.
            var tickAfterLanding = samples[landedAtTick + 1];
            Assert.Equal(Vector3.Zero, tickAfterLanding.Velocity);

            var lastSample = samples[^1];
            Assert.True(lastSample.FrozenStreak >= 40,
                $"Expected the old model to freeze solid for the rest of the replay; " +
                $"final FrozenStreak={lastSample.FrozenStreak}");
        }
        finally
        {
            PhysicsDiagnostics.ResetForTest();
        }
    }

    [Fact]
    public void ComposedRoofLanding_NewFix_VelocitySurvivesAndPositionKeepsAdvancing()
    {
        PhysicsDiagnostics.ResetForTest();
        try
        {
            var samples = ReplayRealRoofLandingComposed(
                preserveResidualVelocityOnGroundedTick: true);
            DumpComposed("NEW (residual velocity preserved)", samples);

            int landedAtTick = samples.FindIndex(s => s.OnWalkable);
            Assert.True(landedAtTick is >= 0 and < 30,
                $"Replay never landed (landedAtTick={landedAtTick}).");

            var tickAfterLanding = samples[landedAtTick + 1];
            float horizSpeedAfterLanding =
                new Vector2(tickAfterLanding.Velocity.X, tickAfterLanding.Velocity.Y).Length();
            Assert.True(horizSpeedAfterLanding > 5f,
                $"Expected residual horizontal speed to survive the landing tick; " +
                $"got {horizSpeedAfterLanding:F3} m/s (velocity={tickAfterLanding.Velocity})");

            // The mover must never freeze solid for the remainder of the
            // replay — this is the "no permanent freeze" acceptance bar. A
            // few zero-advance ticks are tolerated (e.g. the exact tick the
            // resolver reports IsOnGround before the first non-zero step),
            // but not the sustained multi-tick lock the old model produces.
            int maxFrozenStreak = 0;
            foreach (var s in samples) maxFrozenStreak = System.Math.Max(maxFrozenStreak, s.FrozenStreak);
            Assert.True(maxFrozenStreak < 10,
                $"Expected continued advance (no sustained freeze); " +
                $"maxFrozenStreak={maxFrozenStreak}");

            // The body must have travelled a meaningful distance across the
            // roof after landing, not just sat at the impact point.
            var lastSample = samples[^1];
            float totalPostLandingTravel = Vector3.Distance(
                samples[landedAtTick].Pos, lastSample.Pos);
            Assert.True(totalPostLandingTravel > 1.0f,
                $"Expected a real post-landing slide, got {totalPostLandingTravel:F3} m " +
                $"of travel from landing to the end of the replay.");
        }
        finally
        {
            PhysicsDiagnostics.ResetForTest();
        }
    }

    [Fact]
    public void ComposedRoofLanding_NewFix_SyntheticGrazingApproach_DecaysViaCalcFriction()
    {
        PhysicsDiagnostics.ResetForTest();
        try
        {
            var engine = MakeRoofEngine();
            const float dt = 1f / TicksPerSecond;

            var body = new PhysicsBody { TransientState = TransientStateFlags.Active };
            Vector3 approachVel = new Vector3(0.4286f, -0.2857f, 0f);
            approachVel = Vector3.Normalize(approachVel) * 6f;
            body.Position = new Vector3(0f, 0f, 12f);
            body.Velocity = new Vector3(approachVel.X, approachVel.Y, -6f);
            uint cell = CellId;

            var samples = new List<ComposedTickSample>();
            int ticksSinceGrounded = -1;
            for (int tick = 0; tick < 80; tick++)
            {
                Vector3 preIntegratePos = body.Position;
                bool onGroundBeforeResolve = body.OnWalkable;

                body.calc_acceleration();
                body.UpdatePhysicsInternal(dt);

                Vector3 postIntegratePos = body.Position;
                bool candidateMoved = postIntegratePos != preIntegratePos;

                var result = engine.ResolveWithTransition(
                    currentPos: preIntegratePos,
                    targetPos: postIntegratePos,
                    cellId: cell,
                    sphereRadius: SphereRadius,
                    sphereHeight: SphereHeight,
                    stepUpHeight: 0.6f,
                    stepDownHeight: 1.5f,
                    isOnGround: onGroundBeforeResolve,
                    body: body,
                    moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                    movingEntityId: 0x01000000u);

                float advance = Vector3.Distance(result.Position, preIntegratePos);
                bool prevContact = body.InContact;
                bool prevOnWalkable = body.OnWalkable;
                body.Position = result.Position;
                cell = result.CellId;

                if (result.IsOnGround && body.Velocity.Z <= 0f)
                {
                    body.TransientState |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
                    body.calc_acceleration();
                    if (body.Velocity.Z < 0f)
                        body.Velocity = new Vector3(body.Velocity.X, body.Velocity.Y, 0f);
                }
                else
                {
                    body.TransientState &= ~(TransientStateFlags.Contact | TransientStateFlags.OnWalkable);
                    body.calc_acceleration();
                }

                if (candidateMoved)
                {
                    PhysicsObjUpdate.HandleAllCollisions(
                        body,
                        result.CollisionNormalValid, result.CollisionNormal,
                        prevContact, prevOnWalkable, nowOnWalkable: body.OnWalkable);
                }

                samples.Add(new ComposedTickSample(
                    tick, body.Position, body.Velocity, advance,
                    result.CollisionNormalValid, result.CollisionNormal,
                    body.OnWalkable, 0));

                if (!onGroundBeforeResolve && body.OnWalkable)
                    ticksSinceGrounded = 0;
                else if (body.OnWalkable)
                {
                    ticksSinceGrounded++;
                    if (ticksSinceGrounded >= 40)
                        break;
                }
            }

            DumpComposed("SYNTHETIC grazing approach (dot < 0.25 expected)", samples);

            int landedAtTick = samples.FindIndex(s => s.OnWalkable);
            Assert.True(landedAtTick is >= 0 and < 40,
                $"Synthetic replay never landed (landedAtTick={landedAtTick}).");

            float speedAtLanding =
                new Vector2(samples[landedAtTick].Velocity.X, samples[landedAtTick].Velocity.Y).Length();
            float speedAtEnd =
                new Vector2(samples[^1].Velocity.X, samples[^1].Velocity.Y).Length();

            Assert.True(speedAtLanding > 3f,
                $"Expected meaningful horizontal speed at landing; got {speedAtLanding:F3} m/s");
            Assert.True(speedAtEnd < speedAtLanding * 0.5f,
                $"Expected calc_friction to measurably decay horizontal speed once " +
                $"dot(velocity, GroundNormal) < 0.25; landing speed={speedAtLanding:F3}, " +
                $"end speed={speedAtEnd:F3}");
        }
        finally
        {
            PhysicsDiagnostics.ResetForTest();
        }
    }

    [Fact]
    public void UphillLanding_Synthetic_ReflectionDecisionUnaffectedByResidualVelocityFix()
    {
        // A 30-degree uphill-facing slope: outward normal tilts toward -X
        // (the "downhill" horizontal direction, see the research doc
        // addendum), so a mover approaching in +X is moving UPHILL into it.
        float slopeRad = 30f * MathF.PI / 180f;
        Vector3 uphillNormal = new(-MathF.Sin(slopeRad), 0f, MathF.Cos(slopeRad));

        (Vector3 finalVelocity, bool onWalkableAfterLanding) RunOnce(
            bool preserveResidualVelocityOnGroundedTick)
        {
            var body = new PhysicsBody { TransientState = TransientStateFlags.Active };
            body.Velocity = new Vector3(5f, 0f, -2f);
            body.GroundNormal = uphillNormal;

            bool prevContact = body.InContact;
            bool prevOnWalkable = body.OnWalkable; // false: was airborne

            body.TransientState |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
            body.calc_acceleration();
            if (body.Velocity.Z < 0f)
                body.Velocity = new Vector3(body.Velocity.X, body.Velocity.Y, 0f);

            PhysicsObjUpdate.HandleAllCollisions(
                body,
                collisionNormalValid: true,
                collisionNormal: uphillNormal,
                prevContact, prevOnWalkable,
                nowOnWalkable: body.OnWalkable);

            if (body.OnWalkable && !preserveResidualVelocityOnGroundedTick)
            {
                float savedVz = body.Velocity.Z;
                body.Velocity = new Vector3(0f, 0f, savedVz);
            }

            return (body.Velocity, body.OnWalkable);
        }

        var (oldModelVelocity, oldOnWalkable) = RunOnce(preserveResidualVelocityOnGroundedTick: false);
        var (newModelVelocityBeforeToggle, _) = RunOnce(preserveResidualVelocityOnGroundedTick: true);

        _out.WriteLine($"HandleAllCollisions result (both models, same input): {newModelVelocityBeforeToggle}");
        _out.WriteLine($"Old model's next-tick view (zeroed if OnWalkable): {oldModelVelocity}");

        Assert.Equal(
            oldModelVelocity.Z > 0.01f,
            newModelVelocityBeforeToggle.Z > 0.01f);

        bool reflected = newModelVelocityBeforeToggle.Z > 0.01f;
        _out.WriteLine(reflected
            ? "REFLECTED: HandleAllCollisions bounced this uphill landing (byte-exact retail " +
              "shouldReflect = !(prevOnWalkable && nowOnWalkable && !sledding); prevOnWalkable=false " +
              "here makes shouldReflect true regardless of destination walkability -- confirmed " +
              "pre-existing, existing closed mechanism, NOT introduced or worsened by this change)."
            : "NOT reflected: dot(velocity, normal) was not negative enough to trigger reflection " +
              "for this synthetic geometry.");
    }
}
