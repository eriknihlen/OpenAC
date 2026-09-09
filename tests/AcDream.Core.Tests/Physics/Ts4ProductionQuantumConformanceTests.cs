using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public sealed class Ts4ProductionQuantumConformanceTests
{
    private const uint Cell = 0xA9B40001u;
    private const uint GfxId = 0x0100E1B0u;
    private const float Dt = 1f / 30f;
    private const float Radius = 0.48f;
    private const int TickCount = 90;
    private const ushort RoofPolygonId = 1;

    private static readonly ImmutableArray<FlatCollisionSphere> HumanSpheres =
        ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(0f, 0f, 0.475f), Radius),
            new FlatCollisionSphere(new Vector3(0f, 0f, 1.350f), Radius));

    private readonly ITestOutputHelper _output;

    public Ts4ProductionQuantumConformanceTests(ITestOutputHelper output) =>
        _output = output;

    [Theory]
    [InlineData("vertical", 0f, 0f, 0f, 0xC18415BEu, 0x00000000u, 0xC201B112u, 0x00000000u, 0x00000000u, 0xC1EB3325u)]
    [InlineData("inward", 0.5366564f, 0f, -0.2683282f, 0xC18464EAu, 0x00000000u, 0xC202003Eu, 0x3F096250u, 0x00000000u, 0xC1ED58AEu)]
    [InlineData("tangent", 0f, 0.30f, 0f, 0xC18415BEu, 0x3F63F3DBu, 0xC201B112u, 0x00000000u, 0x3E99999Au, 0xC1EB3325u)]
    [InlineData("downhill", -0.2683282f, 0f, -0.5366564f, 0xC18A74DBu, 0x00000000u, 0xC208102Fu, 0xBE896250u, 0x00000000u, 0xC1EF7E37u)]
    public void RoofDirections_ProductionQuantum_GraphFlatExactAndNeverWedge(
        string direction,
        float velocityX,
        float velocityY,
        float velocityZ,
        uint terminalPositionXBits,
        uint terminalPositionYBits,
        uint terminalPositionZBits,
        uint terminalVelocityXBits,
        uint terminalVelocityYBits,
        uint terminalVelocityZBits)
    {
        var initialPosition = new Vector3(0.5f, 0f, 3f);
        var initialVelocity = new Vector3(velocityX, velocityY, velocityZ);
        QuantumTrace graph = RunTrace(
            preparedFlat: false,
            initialPosition,
            initialVelocity,
            TickCount);
        QuantumTrace flat = RunTrace(
            preparedFlat: true,
            initialPosition,
            initialVelocity,
            TickCount);

        AssertTraceExact(graph, flat);
        AssertNoSteepSurfaceFixedPoint(graph, direction);
        AssertNoSlopePenetration(graph, direction);
        Assert.True(graph.FirstContactTick >= 0,
            $"{direction} never contacted the authored steep roof.");
        AssertCollisionResponseExact(graph.Frames[graph.FirstContactTick]);
        AssertTerminalStateExact(
            graph,
            terminalPositionXBits,
            terminalPositionYBits,
            terminalPositionZBits,
            terminalVelocityXBits,
            terminalVelocityYBits,
            terminalVelocityZBits);

        _output.WriteLine(
            $"{direction}: peak={graph.PeakZ:R}, contactTick={graph.FirstContactTick}, " +
            $"terminalPos={graph.Frames[^1].Position}, " +
            $"terminalVelocity={graph.Frames[^1].Velocity}, " +
            $"terminalFlags={graph.Frames[^1].TransientState}, " +
            $"terminalSliding={graph.Frames[^1].SlidingNormal}");
    }

    [Fact]
    public void UphillPositiveZJump_ProductionQuantum_GraphFlatExactWithoutLaunch()
    {
        Vector3 initialPosition = new(-0.20f, 0f, 0.422f);
        Vector3 initialVelocity = new(0.4472136f, 0f, 0.8944272f);
        QuantumTrace graph = RunTrace(
            preparedFlat: false,
            initialPosition,
            initialVelocity,
            TickCount);
        QuantumTrace flat = RunTrace(
            preparedFlat: true,
            initialPosition,
            initialVelocity,
            TickCount);

        AssertTraceExact(graph, flat);
        Assert.True(graph.FirstContactTick >= 0,
            $"The uphill jump never hit the roof; peak={graph.PeakZ:R}, " +
            $"terminal={graph.Frames[^1].Position}, " +
            $"collisionTick={graph.Frames.FindIndex(frame => frame.CollisionNormalValid)}.");
        AssertNoSteepSurfaceFixedPoint(graph, "uphill-jump");
        AssertNoSlopePenetration(graph, "uphill-jump");

        QuantumFrame hit = graph.Frames[graph.FirstContactTick];
        int peakFrame = graph.Frames.FindIndex(frame => frame.Position.Z == graph.PeakZ);
        Assert.InRange(peakFrame, 1, graph.FirstContactTick - 1);
        Assert.True(hit.CandidateVelocity.Z < 0f,
            $"The jump must contact after its real apex: {hit.CandidateVelocity}.");
        Assert.All(
            graph.Frames.GetRange(
                graph.FirstContactTick,
                graph.Frames.Count - graph.FirstContactTick),
            frame => Assert.True(frame.CandidateVelocity.Z <= 0f,
                $"The foot-contact path created an upward relaunch at frame " +
                $"{frame.Tick}: {frame.CandidateVelocity}."));
        float postHitPeak = graph.Frames
            .GetRange(graph.FirstContactTick, graph.Frames.Count - graph.FirstContactTick)
            .Max(frame => frame.Position.Z);
        Assert.True(postHitPeak <= graph.PeakZ + 0.001f,
            $"The collision created a second launch: firstPeak={graph.PeakZ:R}, " +
            $"postHitPeak={postHitPeak:R}.");
        AssertCollisionResponseExact(hit);
        Assert.Equal(0x3EECC54Bu, BitConverter.SingleToUInt32Bits(graph.PeakZ));
        Assert.Equal(6, graph.FirstContactTick);
        AssertTerminalStateExact(
            graph,
            0xC18334B2u,
            0x00000000u,
            0xC200D006u,
            0x3EE4F92Eu,
            0x00000000u,
            0xC1E40B5Du);

        _output.WriteLine(
            $"uphill-jump: peak={graph.PeakZ:R}, contactTick={graph.FirstContactTick}, " +
            $"hitCandidateVelocity={hit.CandidateVelocity}, " +
            $"hitPreResponseVelocity={hit.PreResponseVelocity}, " +
            $"hitVelocity={hit.Velocity}, " +
            $"terminalPos={graph.Frames[^1].Position}, " +
            $"terminalVelocity={graph.Frames[^1].Velocity}, " +
            $"terminalFlags={graph.Frames[^1].TransientState}, " +
            $"terminalSliding={graph.Frames[^1].SlidingNormal}");
    }

    [Fact]
    public void PositiveZJump_HeadOnlyCollision_UsesExactRetailElasticReflection()
    {
        var fixture = ElevatedHeadWall();
        Vector3 initialPosition = new(-0.60f, 0f, 0f);
        Vector3 initialVelocity = new(2f, 0f, 2f);
        QuantumTrace graph = RunTrace(
            preparedFlat: false,
            initialPosition,
            initialVelocity,
            ticks: 12,
            fixture);
        QuantumTrace flat = RunTrace(
            preparedFlat: true,
            initialPosition,
            initialVelocity,
            ticks: 12,
            fixture);

        AssertTraceExact(graph, flat);
        int hitIndex = graph.Frames.FindIndex(frame => frame.CollisionNormalValid);
        Assert.InRange(hitIndex, 0, graph.Frames.Count - 1);
        QuantumFrame hit = graph.Frames[hitIndex];
        Assert.True(hit.CandidateVelocity.Z > 0f,
            $"The head-only collision did not occur during positive-Z travel: {hit.CandidateVelocity}.");
        Assert.True(Vector3.Dot(hit.PreResponseVelocity, hit.CollisionNormal) < 0f,
            $"The fixture did not exercise the inward reflection branch: " +
            $"v={hit.PreResponseVelocity}, n={hit.CollisionNormal}.");
        AssertCollisionResponseExact(hit);
        AssertFloatBits(hit.PreResponseVelocity.Z, hit.Velocity.Z);
        Assert.True(hit.Velocity.X < 0f,
            $"The 5%-elastic retail reflection did not reverse the inward component: {hit.Velocity}.");

        for (int i = hitIndex + 1; i < graph.Frames.Count; i++)
        {
            Assert.True(graph.Frames[i].CandidateVelocity.Z
                        < graph.Frames[i - 1].CandidateVelocity.Z,
                $"The head collision manufactured a later vertical launch at frame {i}.");
        }
    }

    private static QuantumTrace RunTrace(
        bool preparedFlat,
        Vector3 initialPosition,
        Vector3 initialVelocity,
        int ticks) =>
        RunTrace(preparedFlat, initialPosition, initialVelocity, ticks, WideSteepRoof());

    private static QuantumTrace RunTrace(
        bool preparedFlat,
        Vector3 initialPosition,
        Vector3 initialVelocity,
        int ticks,
        (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved) fixture)
    {
        PhysicsEngine engine = BuildEngine(fixture, preparedFlat);
        var body = new PhysicsBody
        {
            Position = initialPosition,
            Orientation = Quaternion.Identity,
            Velocity = initialVelocity,
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(Cell, initialPosition, initialPosition);

        var frames = new List<QuantumFrame>(ticks);
        float peakZ = body.Position.Z;
        int firstContactTick = -1;
        uint cell = Cell;
        for (int tick = 0; tick < ticks; tick++)
        {
            Vector3 preIntegratePosition = body.Position;

            body.calc_acceleration();
            body.UpdatePhysicsInternal(Dt);

            Vector3 candidatePosition = body.Position;
            Vector3 candidateVelocity = body.Velocity;
            bool candidateMoved = candidatePosition != preIntegratePosition;
            bool onGroundBeforeResolve = body.OnWalkable;
            ResolveResult result = engine.ResolveWithTransition(
                preIntegratePosition,
                candidatePosition,
                cell,
                Radius,
                sphereHeight: 1.835f,
                stepUpHeight: 0.4f,
                stepDownHeight: 0.4f,
                isOnGround: onGroundBeforeResolve,
                body,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000000u,
                sphereList: HumanSpheres,
                sphereScale: 1f);

            Vector3 preResponseVelocity = body.Velocity;
            int preResponseStationaryFall = body.FramesStationaryFall;

            bool previousContact = body.InContact;
            bool previousOnWalkable = body.OnWalkable;

            body.CachedVelocity = candidateMoved
                ? (result.Position - preIntegratePosition) / Dt
                : Vector3.Zero;
            body.CommitTransitionPosition(result.CellId, result.Position);
            cell = result.CellId;

            bool commitApplied = result.Ok && candidateMoved;
            if (commitApplied)
            {
                PhysicsObjUpdate.CommitSetPositionTransition(
                    body,
                    result.InContact,
                    result.OnWalkable,
                    result.CollisionNormalValid,
                    result.CollisionNormal,
                    previousContact,
                    previousOnWalkable);
            }

            peakZ = MathF.Max(peakZ, body.Position.Z);
            if (firstContactTick < 0 && result.InContact)
                firstContactTick = tick;
            frames.Add(CaptureFrame(
                tick,
                candidatePosition,
                candidateVelocity,
                preResponseVelocity,
                preResponseStationaryFall,
                previousOnWalkable,
                candidateMoved,
                commitApplied,
                result,
                body));
        }

        return new QuantumTrace(frames, peakZ, firstContactTick);
    }

    private static (
        PhysicsBSPNode Root,
        Dictionary<ushort, ResolvedPolygon> Resolved) WideSteepRoof()
    {
        Vector3[] vertices =
        [
            new(-64f, -64f, -128f),
            new( 64f, -64f,  128f),
            new( 64f,  64f,  128f),
            new(-64f,  64f, -128f),
        ];
        Vector3 normal = Vector3.Normalize(
            Vector3.Cross(vertices[1] - vertices[0], vertices[3] - vertices[0]));
        if (normal.X > 0f)
            normal = -normal;
        float d = -Vector3.Dot(normal, vertices[0]);

        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 192f },
        };
        root.Polygons.Add(RoofPolygonId);
        return (root, new Dictionary<ushort, ResolvedPolygon>
        {
            [RoofPolygonId] = new ResolvedPolygon
            {
                Id = RoofPolygonId,
                Vertices = vertices,
                Plane = new Plane(normal, d),
                NumPoints = vertices.Length,
                SidesType = CullMode.None,
            },
        });
    }

    private static (
        PhysicsBSPNode Root,
        Dictionary<ushort, ResolvedPolygon> Resolved) ElevatedHeadWall()
    {
        Vector3[] vertices =
        [
            new(0f,  64f,  1.10f),
            new(0f, -64f,  1.10f),
            new(0f, -64f, 64.00f),
            new(0f,  64f, 64.00f),
        ];
        var plane = new Plane(-Vector3.UnitX, 0f);
        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = new Vector3(0f, 0f, 32f),
                Radius = 96f,
            },
        };
        root.Polygons.Add(RoofPolygonId);
        return (root, new Dictionary<ushort, ResolvedPolygon>
        {
            [RoofPolygonId] = new ResolvedPolygon
            {
                Id = RoofPolygonId,
                Vertices = vertices,
                Plane = plane,
                NumPoints = vertices.Length,
                SidesType = CullMode.None,
            },
        });
    }

    private static PhysicsEngine BuildEngine(
        (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved) fixture,
        bool preparedFlat)
    {
        var normalized = new Dictionary<ushort, ResolvedPolygon>(fixture.Resolved.Count);
        foreach ((ushort id, ResolvedPolygon polygon) in fixture.Resolved)
        {
            normalized.Add(id, new ResolvedPolygon
            {
                Id = id,
                Vertices = polygon.Vertices,
                Plane = polygon.Plane,
                NumPoints = polygon.NumPoints,
                SidesType = polygon.SidesType,
            });
        }

        var physics = new GfxObjPhysics
        {
            SourceId = GfxId,
            BSP = new PhysicsBSPTree { Root = fixture.Root },
            Resolved = normalized,
            BoundingSphere = fixture.Root.BoundingSphere,
        };
        var cache = new PhysicsDataCache();
        if (preparedFlat)
        {
            cache.CollisionTraversalMode = CollisionTraversalMode.Flat;
            cache.CacheGfxObj(GfxId, FlatCollisionAssetBuilder.FlattenGfxObj(physics));
        }
        else
        {
            cache.RegisterGfxObjForTest(GfxId, physics);
        }

        var heights = new byte[81];
        var heightTable = new float[256];
        Array.Fill(heightTable, -1000f);
        var engine = new PhysicsEngine { DataCache = cache };
        engine.AddLandblock(
            0xA9B40000u,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        engine.AddLandblock(
            0xA8B40000u,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            -192f,
            0f);
        engine.ShadowObjects.Register(
            GfxId,
            GfxId,
            Vector3.Zero,
            Quaternion.Identity,
            fixture.Root.BoundingSphere.Radius,
            0f,
            0f,
            0xA9B4FFFFu,
            ShadowCollisionType.BSP,
            1f);
        return engine;
    }

    private static void AssertTraceExact(QuantumTrace graph, QuantumTrace flat)
    {
        Assert.Equal(graph.PeakZ, flat.PeakZ);
        Assert.Equal(graph.FirstContactTick, flat.FirstContactTick);
        Assert.Equal(graph.Frames.Count, flat.Frames.Count);
        for (int i = 0; i < graph.Frames.Count; i++)
            Assert.Equal(graph.Frames[i].Bits, flat.Frames[i].Bits);
    }

    private static void AssertNoSteepSurfaceFixedPoint(
        QuantumTrace trace,
        string direction)
    {
        int frozenStreak = 0;
        for (int i = 1; i < trace.Frames.Count; i++)
        {
            QuantumFrame previous = trace.Frames[i - 1];
            QuantumFrame current = trace.Frames[i];
            bool onSlope = IsOverSlope(current.Position)
                           && current.ContactPlaneValid
                           && current.ContactPlane.Normal.Z < PhysicsGlobals.FloorZ;
            frozenStreak = onSlope
                           && Vector3.Distance(previous.Position, current.Position) < 0.001f
                ? frozenStreak + 1
                : 0;
            Assert.True(frozenStreak <= 15,
                $"{direction} fixed on the steep roof for {frozenStreak} ticks at " +
                $"frame {i}, position={current.Position}.");
        }
    }

    private static void AssertNoSlopePenetration(QuantumTrace trace, string direction)
    {
        Plane slope = WideSteepRoof().Resolved[RoofPolygonId].Plane;
        for (int i = 0; i < trace.Frames.Count; i++)
        {
            QuantumFrame frame = trace.Frames[i];
            Assert.True(float.IsFinite(frame.Position.X)
                        && float.IsFinite(frame.Position.Y)
                        && float.IsFinite(frame.Position.Z),
                $"{direction} produced a non-finite position at frame {i}: {frame.Position}.");
            if (!IsOverSlope(frame.Position))
                continue;

            Vector3 footCenter = frame.Position + HumanSpheres[0].Origin;
            float signedDistance = Vector3.Dot(slope.Normal, footCenter) + slope.D;
            Assert.True(signedDistance >= Radius - 0.015f,
                $"{direction} penetrated the roof at frame {i}: " +
                $"distance={signedDistance:R}, position={frame.Position}.");
        }
    }

    private static bool IsOverSlope(Vector3 position) =>
        position.X is >= -64f and <= 64f && MathF.Abs(position.Y) <= 64f;

    private static void AssertCollisionResponseExact(QuantumFrame hit)
    {
        Assert.True(hit.ResultOk,
            $"Frame {hit.Tick} reported contact without an accepted transition.");
        Assert.True(hit.CandidateMoved,
            $"Frame {hit.Tick} reported contact without a moving candidate.");
        Assert.True(hit.CommitApplied,
            $"Frame {hit.Tick} did not execute the production commit/response gate.");

        Vector3 expected = hit.PreResponseVelocity;
        bool shouldReflect = !hit.PreviousOnWalkable || !hit.BodyOnWalkable;
        if (hit.PreResponseStationaryFall > 1)
        {
            expected = Vector3.Zero;
        }
        else if (shouldReflect && hit.CollisionNormalValid)
        {
            float dot = Vector3.Dot(expected, hit.CollisionNormal);
            if (dot < 0f)
                expected += hit.CollisionNormal * (-(dot * 1.05f));
        }

        AssertVectorBits(expected, hit.Velocity);
    }

    private static void AssertTerminalStateExact(
        QuantumTrace trace,
        uint positionXBits,
        uint positionYBits,
        uint positionZBits,
        uint velocityXBits,
        uint velocityYBits,
        uint velocityZBits)
    {
        QuantumFrame terminal = trace.Frames[^1];
        Assert.Equal(positionXBits, BitConverter.SingleToUInt32Bits(terminal.Position.X));
        Assert.Equal(positionYBits, BitConverter.SingleToUInt32Bits(terminal.Position.Y));
        Assert.Equal(positionZBits, BitConverter.SingleToUInt32Bits(terminal.Position.Z));
        Assert.Equal(velocityXBits, BitConverter.SingleToUInt32Bits(terminal.Velocity.X));
        Assert.Equal(velocityYBits, BitConverter.SingleToUInt32Bits(terminal.Velocity.Y));
        Assert.Equal(velocityZBits, BitConverter.SingleToUInt32Bits(terminal.Velocity.Z));
        Assert.Equal(TransientStateFlags.Active | TransientStateFlags.Contact,
            terminal.TransientState);
        Assert.True(terminal.BodyInContact);
        Assert.False(terminal.BodyOnWalkable);
        Assert.False(terminal.BodySliding);
        Assert.Equal(Vector3.Zero, terminal.SlidingNormal);
        Assert.False(terminal.CollisionNormalValid);
        Assert.True(terminal.ContactPlaneValid);

        Assert.Equal(0xBF64F92Fu,
            BitConverter.SingleToUInt32Bits(terminal.ContactPlane.Normal.X));
        Assert.Equal(0x00000000u,
            BitConverter.SingleToUInt32Bits(terminal.ContactPlane.Normal.Y));
        Assert.Equal(0x3EE4F92Fu,
            BitConverter.SingleToUInt32Bits(terminal.ContactPlane.Normal.Z));
        Assert.Equal(0x80000000u,
            BitConverter.SingleToUInt32Bits(terminal.ContactPlane.D));
    }

    private static QuantumFrame CaptureFrame(
        int tick,
        Vector3 candidatePosition,
        Vector3 candidateVelocity,
        Vector3 preResponseVelocity,
        int preResponseStationaryFall,
        bool previousOnWalkable,
        bool candidateMoved,
        bool commitApplied,
        ResolveResult result,
        PhysicsBody body)
    {
        var bits = new StringBuilder(768);
        Append(bits, tick);
        Append(bits, candidatePosition);
        Append(bits, candidateVelocity);
        Append(bits, preResponseVelocity);
        Append(bits, preResponseStationaryFall);
        Append(bits, candidateMoved);
        Append(bits, commitApplied);
        Append(bits, result.Position);
        Append(bits, result.CellId);
        Append(bits, result.IsOnGround);
        Append(bits, result.CollisionNormalValid);
        Append(bits, result.CollisionNormal);
        Append(bits, result.Ok);
        Append(bits, result.Orientation);
        Append(bits, result.InContact);
        Append(bits, result.OnWalkable);
        Append(bits, body.Position);
        Append(bits, body.CellPosition.ObjCellId);
        Append(bits, body.CellPosition.Frame.Origin);
        Append(bits, body.CellPosition.Frame.Orientation);
        Append(bits, body.Velocity);
        Append(bits, body.CachedVelocity);
        Append(bits, body.Acceleration);
        Append(bits, body.GroundNormal);
        Append(bits, body.SlidingNormal);
        Append(bits, body.ContactPlaneValid);
        Append(bits, body.ContactPlane);
        Append(bits, body.ContactPlaneCellId);
        Append(bits, body.ContactPlaneIsWater);
        Append(bits, body.WalkablePolygonValid);
        Append(bits, body.WalkablePlane);
        Append(bits, body.WalkableUp);
        Append(bits, body.FramesStationaryFall);
        Append(bits, (uint)body.State);
        Append(bits, (uint)body.TransientState);

        return new QuantumFrame(
            tick,
            candidatePosition,
            candidateVelocity,
            preResponseVelocity,
            preResponseStationaryFall,
            candidateMoved,
            result.Ok,
            commitApplied,
            body.Position,
            body.Velocity,
            body.TransientState,
            body.InContact,
            body.OnWalkable,
            (body.TransientState & TransientStateFlags.Sliding) != 0,
            body.SlidingNormal,
            result.CollisionNormalValid,
            result.CollisionNormal,
            previousOnWalkable,
            body.ContactPlaneValid,
            body.ContactPlane,
            bits.ToString());
    }

    private static void Append(StringBuilder target, bool value) =>
        target.Append(value ? "1|" : "0|");

    private static void Append(StringBuilder target, int value) =>
        target.Append(value).Append('|');

    private static void Append(StringBuilder target, uint value) =>
        target.Append(value.ToString("X8")).Append('|');

    private static void Append(StringBuilder target, float value) =>
        Append(target, BitConverter.SingleToUInt32Bits(value));

    private static void Append(StringBuilder target, Vector3 value)
    {
        Append(target, value.X);
        Append(target, value.Y);
        Append(target, value.Z);
    }

    private static void Append(StringBuilder target, Quaternion value)
    {
        Append(target, value.X);
        Append(target, value.Y);
        Append(target, value.Z);
        Append(target, value.W);
    }

    private static void Append(StringBuilder target, Plane value)
    {
        Append(target, value.Normal);
        Append(target, value.D);
    }

    private static void AssertFloatBits(float expected, float actual) =>
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected),
            BitConverter.SingleToUInt32Bits(actual));

    private static void AssertVectorBits(Vector3 expected, Vector3 actual)
    {
        AssertFloatBits(expected.X, actual.X);
        AssertFloatBits(expected.Y, actual.Y);
        AssertFloatBits(expected.Z, actual.Z);
    }

    private sealed record QuantumTrace(
        List<QuantumFrame> Frames,
        float PeakZ,
        int FirstContactTick);

    private sealed record QuantumFrame(
        int Tick,
        Vector3 CandidatePosition,
        Vector3 CandidateVelocity,
        Vector3 PreResponseVelocity,
        int PreResponseStationaryFall,
        bool CandidateMoved,
        bool ResultOk,
        bool CommitApplied,
        Vector3 Position,
        Vector3 Velocity,
        TransientStateFlags TransientState,
        bool BodyInContact,
        bool BodyOnWalkable,
        bool BodySliding,
        Vector3 SlidingNormal,
        bool CollisionNormalValid,
        Vector3 CollisionNormal,
        bool PreviousOnWalkable,
        bool ContactPlaneValid,
        Plane ContactPlane,
        string Bits);
}
