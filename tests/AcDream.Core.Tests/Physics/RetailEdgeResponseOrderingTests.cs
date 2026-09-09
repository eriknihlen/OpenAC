using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailEdgeResponseOrderingTests
{
    private const uint Cell = 0xA9B40001u;

    [Fact]
    public void TransitionalInsert_ValidSteepContact_ReturnsBeforeOrdinaryStepDownTail()
    {
        Vector3 current = new(2f, 3f, 4f);
        Vector3 target = current + new Vector3(0.1f, 0f, 0f);
        var transition = BSPStepUpFixtures.MakeGroundedTransition(current, target, cellId: Cell);
        transition.ObjectInfo.State |= ObjectInfoState.EdgeSlide;
        transition.ObjectInfo.StepDown = true;
        transition.ObjectInfo.StepDownHeight = 2f;

        Vector3 untouchedBackup = new(97f, 98f, 99f);
        const uint untouchedBackupCell = 0xA9B40044u;
        transition.SpherePath.BackupCheckPos = untouchedBackup;
        transition.SpherePath.BackupCheckCellId = untouchedBackupCell;

        var steep = new Plane(Vector3.Normalize(new Vector3(1f, 0f, 0.25f)), 0f);
        var engine = new PhysicsEngine
        {
            TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
            {
                if (phase == TransitionCellCollisionPhase.Objects)
                    candidate.CollisionInfo.SetContactPlane(steep, Cell, isWater: true);
                return actual;
            },
        };

        TransitionState result = transition.TransitionalInsertForTest(1, engine);

        Assert.Equal(TransitionState.OK, result);
        Assert.True(transition.CollisionInfo.ContactPlaneValid);
        Assert.True(transition.CollisionInfo.ContactPlaneIsWater);
        Assert.Equal(steep, transition.CollisionInfo.ContactPlane);
        Assert.Equal(untouchedBackup, transition.SpherePath.BackupCheckPos);
        Assert.Equal(untouchedBackupCell, transition.SpherePath.BackupCheckCellId);
    }

    [Theory]
    [InlineData(1, 0.5f, 2.0f, 0.25f, 1)]
    [InlineData(2, 0.5f, 2.0f, 1.00f, 2)]
    [InlineData(1, 0.5f, 0.75f, 0.75f, 1)]
    [InlineData(2, 0.5f, 0.75f, 0.75f, 1)]
    public void StepDownProbePlan_PreservesRetailOneVersusTwoSphereSplit(
        int sphereCount,
        float radius,
        float requestedHeight,
        float expectedProbeHeight,
        int expectedProbeCount)
    {
        (float probeHeight, int probeCount) = Transition.GetStepDownProbePlan(
            sphereCount,
            radius,
            requestedHeight);

        Assert.Equal(expectedProbeHeight, probeHeight);
        Assert.Equal(expectedProbeCount, probeCount);
    }

    [Fact]
    public void EdgeSlide_NotOnWalkableSteepContact_RestoresBeforeCliffSlide()
    {
        var transition = MakeFailedStepDownTransition();
        transition.ObjectInfo.State = ObjectInfoState.EdgeSlide;
        transition.CollisionInfo.ContactPlaneValid = true;
        transition.CollisionInfo.ContactPlane =
            new Plane(Vector3.Normalize(new Vector3(1f, 0f, 0.25f)), 0f);
        transition.CollisionInfo.ContactPlaneIsWater = true;
        transition.CollisionInfo.LastKnownContactPlaneValid = true;
        transition.CollisionInfo.LastKnownContactPlane = new Plane(Vector3.UnitZ, 0f);

        Vector3 failedCandidate = transition.SpherePath.BackupCheckPos;
        bool stop = transition.EdgeSlideAfterStepDownFailedForTest(
            new PhysicsEngine(),
            stepDownHeight: 0.04f,
            zVal: PhysicsGlobals.FloorZ,
            out TransitionState result);

        Assert.True(stop);
        Assert.Equal(TransitionState.OK, result);
        Assert.Equal(failedCandidate, transition.SpherePath.CheckPos);
        Assert.False(transition.CollisionInfo.ContactPlaneValid);
        Assert.False(transition.CollisionInfo.ContactPlaneIsWater);
        Assert.False(transition.CollisionInfo.CollisionNormalValid);
    }

    [Fact]
    public void CliffSlide_UsesOnlyLastKnownContactPlaneNormal()
    {
        var transition = MakeFailedStepDownTransition();

        Plane rememberedWalkable = new(Vector3.Normalize(new Vector3(0f, 1f, 1f)), 0f);
        transition.SpherePath.SetWalkable(
            rememberedWalkable,
            SquareOnPlaneZ0(),
            Vector3.UnitZ);
        transition.SpherePath.ClearWalkable();

        transition.CollisionInfo.LastKnownContactPlaneValid = true;
        transition.CollisionInfo.LastKnownContactPlane = new Plane(Vector3.UnitZ, 0f);
        Plane steepContact = new(Vector3.Normalize(new Vector3(1f, 0f, 0.5f)), 0f);

        TransitionState result = transition.CliffSlideForTest(steepContact);

        Assert.Equal(TransitionState.Adjusted, result);
        Assert.True(transition.CollisionInfo.CollisionNormalValid);
        Assert.True(Vector3.Distance(-Vector3.UnitX, transition.CollisionInfo.CollisionNormal) < 0.0001f);
    }

    [Fact]
    public void CliffSlide_InvalidDefaultLastKnownPlane_TakesDegenerateOkReturn()
    {
        var transition = MakeFailedStepDownTransition();
        transition.CollisionInfo.LastKnownContactPlaneValid = false;
        transition.CollisionInfo.LastKnownContactPlane = default;
        Plane steepContact = new(Vector3.Normalize(new Vector3(1f, 0f, 0.5f)), 0f);

        TransitionState result = transition.CliffSlideForTest(steepContact);

        Assert.Equal(TransitionState.OK, result);
        Assert.False(transition.CollisionInfo.CollisionNormalValid);
    }

    [Fact]
    public void EdgeSlide_StoredSteepWalkable_AlwaysRoutesToPrecipiceSlide()
    {
        var transition = MakeFailedStepDownTransition();
        transition.ObjectInfo.State =
            ObjectInfoState.Contact | ObjectInfoState.OnWalkable | ObjectInfoState.EdgeSlide;
        transition.CollisionInfo.ContactPlaneValid = false;
        transition.CollisionInfo.LastKnownContactPlaneValid = true;
        transition.CollisionInfo.LastKnownContactPlane = new Plane(Vector3.UnitZ, 0f);

        Vector3 steepNormal = Vector3.Normalize(new Vector3(-2f, 0f, 1f));
        var steepPlane = new Plane(steepNormal, 0f);
        Vector3[] steepQuad =
        [
            new(0f, -1f, 0f),
            new(1f, -1f, 2f),
            new(1f,  1f, 2f),
            new(0f,  1f, 0f),
        ];
        transition.SpherePath.SetWalkable(steepPlane, steepQuad, Vector3.UnitZ);

        Vector3 failedCandidate = new(0.5f, 0f, 1f);
        transition.SpherePath.SetCheckPos(failedCandidate, Cell);
        transition.SpherePath.SaveCheckPos();
        transition.SpherePath.AddOffsetToCheckPos(new Vector3(0f, 0f, -0.25f));

        bool stop = transition.EdgeSlideAfterStepDownFailedForTest(
            new PhysicsEngine(),
            stepDownHeight: 0.04f,
            zVal: PhysicsGlobals.FloorZ,
            out TransitionState result);

        Assert.True(stop);
        Assert.Equal(TransitionState.Collided, result);
        Assert.Equal(failedCandidate, transition.SpherePath.CheckPos);
        Assert.False(transition.SpherePath.HasWalkablePolygon);
        Assert.False(transition.CollisionInfo.CollisionNormalValid);
    }

    [Fact]
    public void TransitionalInsert_DegenerateCliffSlideOk_ContinuesOuterRetry()
    {
        Vector3 current = new(2f, 3f, 4f);
        Vector3 target = current + new Vector3(0.1f, 0f, 0f);
        var transition = BSPStepUpFixtures.MakeGroundedTransition(current, target, cellId: Cell);
        transition.ObjectInfo.State |= ObjectInfoState.EdgeSlide;
        transition.ObjectInfo.StepDown = true;
        transition.ObjectInfo.StepDownHeight = 0.04f;

        Plane steep = new(Vector3.Normalize(new Vector3(1f, 0f, 0.25f)), 0f);
        int outerObjectPasses = 0;
        var engine = new PhysicsEngine
        {
            TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
            {
                if (phase != TransitionCellCollisionPhase.Objects)
                    return actual;

                if (candidate.SpherePath.StepDown)
                {
                    candidate.CollisionInfo.SetContactPlane(steep, Cell);
                }
                else
                {
                    outerObjectPasses++;
                    if (outerObjectPasses == 2)
                        candidate.CollisionInfo.SetContactPlane(new Plane(Vector3.UnitZ, 0f), Cell);
                }

                return actual;
            },
        };

        TransitionState result = transition.TransitionalInsertForTest(2, engine);

        Assert.Equal(TransitionState.OK, result);
        Assert.Equal(2, outerObjectPasses);
        Assert.True(transition.CollisionInfo.ContactPlaneValid);
        Assert.Equal(Vector3.UnitZ, transition.CollisionInfo.ContactPlane.Normal);
    }

    [Fact]
    public void MultiFrameSteepRoof_PureVertical_GraphAndFlatSlideDownhillWithoutWedge()
    {
        TraceRun graph = RunSteepRoofTrace(preparedFlat: false, Vector2.Zero);
        TraceRun flat = RunSteepRoofTrace(preparedFlat: true, Vector2.Zero);

        AssertTraceParity(graph, flat);
        Assert.Contains(graph.Frames, frame => frame.Result.InContact);
        AssertNoLongFrozenStreak(graph.Frames, maximumTicks: 15);
        Assert.True(graph.Frames[^1].Result.Position.X
                    < graph.Frames[0].Result.Position.X - 0.20f,
            $"The vertical trace did not descend the roof: " +
            $"{graph.Frames[0].Result.Position} -> {graph.Frames[^1].Result.Position}.");

        Plane slope = BSPStepUpFixtures.SlopedUnwalkable().Resolved[
            BSPStepUpFixtures.SlopedUnwalkable_SlopeId].Plane;
        float radius = BSPStepUpFixtures.SphereRadius;
        for (int i = 0; i < graph.Frames.Count; i++)
        {
            Vector3 position = graph.Frames[i].Result.Position;
            AssertFinite(position, $"steep-roof frame {i}");
            if (i > 0)
            {
                float distance = Vector3.Distance(
                    graph.Frames[i - 1].Result.Position,
                    position);
                Assert.InRange(distance, 0f, 1.1f);
            }

            if (position.X is >= 0f and <= 1f && MathF.Abs(position.Y) <= 1f)
            {
                Vector3 footCenter = position + new Vector3(0f, 0f, radius);
                float signedDistance = Vector3.Dot(slope.Normal, footCenter) + slope.D;
                Assert.True(signedDistance >= radius - 0.015f,
                    $"Steep-roof penetration at frame {i}: distance={signedDistance}, " +
                    $"radius={radius}, position={position}.");
            }
        }
    }

    [Theory]
    [InlineData(-0.30f, 0f, "downhill")]
    [InlineData( 0.30f, 0f, "uphill")]
    [InlineData( 0f, 0.30f, "tangential")]
    public void MultiFrameSteepRoof_DirectionalMotion_RemainsExactAndPreservesRetailResponse(
        float velocityX,
        float velocityY,
        string direction)
    {
        Vector2 horizontalVelocity = new(velocityX, velocityY);
        TraceRun graph = RunSteepRoofTrace(preparedFlat: false, horizontalVelocity);
        TraceRun flat = RunSteepRoofTrace(preparedFlat: true, horizontalVelocity);

        AssertTraceParity(graph, flat);
        Assert.Contains(graph.Frames, frame => frame.Result.InContact);
        AssertNoLongFrozenStreak(graph.Frames, maximumTicks: 15);

        Vector3 first = graph.Frames[0].Result.Position;
        Vector3 last = graph.Frames[^1].Result.Position;
        Vector2 progress = new(last.X - first.X, last.Y - first.Y);
        if (direction == "uphill")
        {
            int contactFrame = graph.Frames.FindIndex(frame => frame.Result.InContact);
            Assert.True(contactFrame >= 0);
            float peakAfterContact = graph.Frames
                .GetRange(contactFrame, graph.Frames.Count - contactFrame)
                .Max(frame => frame.Result.Position.Z);
            Assert.True(peakAfterContact <= graph.Frames[contactFrame].Result.Position.Z + 0.001f,
                $"The uphill trace launched/bounced from the roof: " +
                $"contactZ={graph.Frames[contactFrame].Result.Position.Z}, peak={peakAfterContact}.");
        }
        else
        {
            Assert.True(Vector2.Dot(progress, Vector2.Normalize(horizontalVelocity)) > 0.10f,
                $"The {direction} trace lost requested progress: {first} -> {last}.");
        }

        Plane slope = BSPStepUpFixtures.SlopedUnwalkable().Resolved[
            BSPStepUpFixtures.SlopedUnwalkable_SlopeId].Plane;
        float radius = BSPStepUpFixtures.SphereRadius;
        for (int i = 0; i < graph.Frames.Count; i++)
        {
            Vector3 position = graph.Frames[i].Result.Position;
            AssertFinite(position, $"steep-roof {direction} frame {i}");
            if (i > 0)
            {
                float distance = Vector3.Distance(
                    graph.Frames[i - 1].Result.Position,
                    position);
                Assert.InRange(distance, 0f, 1.1f);
            }

            if (position.X is >= 0f and <= 1f && MathF.Abs(position.Y) <= 1f)
            {
                Vector3 footCenter = position + new Vector3(0f, 0f, radius);
                float signedDistance = Vector3.Dot(slope.Normal, footCenter) + slope.D;
                Assert.True(signedDistance >= radius - 0.015f,
                    $"Steep-roof {direction} penetration at frame {i}: " +
                    $"distance={signedDistance}, radius={radius}, position={position}.");
            }
        }
    }

    [Fact]
    public void MultiFrameFlatRoofLedge_GraphAndFlatTraversalRemainExactAndSlideAlongEdge()
    {
        TraceRun graph = RunFlatRoofLedgeTrace(preparedFlat: false, includeRoof: true);
        TraceRun flat = RunFlatRoofLedgeTrace(preparedFlat: true, includeRoof: true);
        TraceRun noRoof = RunFlatRoofLedgeTrace(preparedFlat: false, includeRoof: false);

        AssertTraceParity(graph, flat);
        Assert.True(graph.LandedFrame > 0, "The collision-backed control never landed on the roof.");
        Assert.Equal(-1, noRoof.LandedFrame);
        Assert.All(graph.Frames.GetRange(0, graph.LandedFrame), frame =>
            Assert.False(frame.Result.OnWalkable));
        TraceFrame landing = graph.Frames[graph.LandedFrame];
        Assert.True(landing.Result.Ok);
        Assert.True(landing.Result.IsOnGround);
        Assert.True(landing.Result.InContact);
        Assert.True(landing.Result.OnWalkable);
        Assert.True(landing.BodyContactPlaneValid);
        Assert.True(landing.BodyWalkablePolygonValid);
        Assert.Equal(Vector3.UnitZ, landing.BodyContactPlane.Normal);
        Assert.Equal(
            TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
            landing.BodyTransientState
            & (TransientStateFlags.Contact | TransientStateFlags.OnWalkable));

        List<TraceFrame> ledge = graph.Frames.GetRange(
            graph.LedgeStartFrame,
            graph.Frames.Count - graph.LedgeStartFrame);
        AssertNoLongFrozenStreak(ledge, maximumTicks: 15);
        int outwardRejectionFrame = ledge.FindIndex(frame =>
            frame.Result.CollisionNormalValid
            && frame.Result.CollisionNormal.X < -0.9f);
        Assert.True(outwardRejectionFrame >= 0,
            "The roof-edge control never produced the expected outward-X rejection normal.");
        float rejectedX = ledge[outwardRejectionFrame].Result.Position.X;
        Assert.All(ledge.GetRange(
                outwardRejectionFrame,
                ledge.Count - outwardRejectionFrame),
            frame => Assert.True(frame.Result.Position.X <= rejectedX + 0.001f,
                $"Outward X resumed after rejection: {frame.Result.Position.X} > {rejectedX}."));
        float unobstructedFinalX = ledge[0].Result.Position.X + 0.12f * (ledge.Count - 1);
        Assert.True(ledge[^1].Result.Position.X < unobstructedFinalX - 0.25f,
            $"Roof edge failed to remove outward travel: final={ledge[^1].Result.Position.X}, " +
            $"unobstructed={unobstructedFinalX}.");
        Assert.True(ledge[^1].Result.Position.Y
                    > ledge[outwardRejectionFrame].Result.Position.Y + 0.25f,
            $"The roof edge rejected all tangency: {ledge[outwardRejectionFrame].Result.Position} -> " +
            $"{ledge[^1].Result.Position}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiFrameGroundedFloorWallSlide_InwardTangentialMotionIsGraphFlatExact(
        bool twoSpheres)
    {
        WallMaintenanceTrace graph = RunGroundedFloorWallSlide(
            preparedFlat: false,
            twoSpheres);
        WallMaintenanceTrace flat = RunGroundedFloorWallSlide(
            preparedFlat: true,
            twoSpheres);

        Assert.Equal(graph.SupportPasses, flat.SupportPasses);
        Assert.Equal(graph.PlacementPasses, flat.PlacementPasses);
        Assert.True(graph.SupportPasses > 0,
            "The full-engine replay never entered grounded support maintenance.");
        Assert.Equal(graph.Frames.Count, graph.SupportPasses);
        Assert.Equal(graph.SupportPasses, graph.PlacementPasses);
        Assert.Equal(graph.Frames.Count, flat.Frames.Count);
        for (int i = 0; i < graph.Frames.Count; i++)
        {
            Assert.Equal(graph.Frames[i].ResolveBits, flat.Frames[i].ResolveBits);
            Assert.Equal(graph.Frames[i].BodyBits, flat.Frames[i].BodyBits);
            Assert.True(graph.Frames[i].Result.Ok,
                $"Grounded wall slide failed at frame {i}.");
            Assert.True(graph.Frames[i].Result.InContact,
                $"Ground contact was lost at frame {i}.");
            Assert.True(graph.Frames[i].Result.OnWalkable,
                $"Walkable state was lost at frame {i}.");

            float wallLimit = 0.5f - BSPStepUpFixtures.SphereRadius
                              + PhysicsGlobals.EPSILON * 10f;
            Assert.True(graph.Frames[i].Result.Position.X <= wallLimit,
                $"Wall penetration at frame {i}: X={graph.Frames[i].Result.Position.X}, " +
                $"limit={wallLimit}.");
        }

        AssertNoLongFrozenStreak(graph.Frames, maximumTicks: 1);
        Assert.True(
            graph.Frames[^1].Result.Position.Y
            > graph.Frames[0].Result.Position.Y + 0.25f,
            $"Wall response removed tangential progress: " +
            $"{graph.Frames[0].Result.Position} -> {graph.Frames[^1].Result.Position}.");
    }

    private static Transition MakeFailedStepDownTransition()
    {
        Vector3 current = Vector3.Zero;
        Vector3 failedCandidate = new(1f, 0f, 0f);
        var transition = BSPStepUpFixtures.MakeGroundedTransition(
            current,
            failedCandidate,
            cellId: Cell);
        transition.ObjectInfo.State |= ObjectInfoState.EdgeSlide;
        transition.SpherePath.SetCheckPos(failedCandidate, Cell);
        transition.SpherePath.SaveCheckPos();
        transition.SpherePath.AddOffsetToCheckPos(new Vector3(0f, 0f, -0.25f));
        return transition;
    }

    private static Vector3[] SquareOnPlaneZ0() =>
    [
        new(-2f, -2f, 0f),
        new( 2f, -2f, 0f),
        new( 2f,  2f, 0f),
        new(-2f,  2f, 0f),
    ];

    private static TraceRun RunSteepRoofTrace(
        bool preparedFlat,
        Vector2 horizontalVelocity)
    {
        var fixture = BSPStepUpFixtures.SlopedUnwalkable();
        PhysicsEngine engine = BuildCollisionEngine(fixture, preparedFlat, 0x0100E101u);
        float radius = BSPStepUpFixtures.SphereRadius;
        const float dt = 1f / 30f;
        const float gravity = -9.8f;
        var body = new PhysicsBody
        {
            Position = new Vector3(0.5f, 0f, 3f),
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            TransientState = TransientStateFlags.Active,
        };
        Vector3 position = new(0.5f, 0f, 3f);
        float velocityZ = 0f;
        var trace = new List<TraceFrame>(90);

        for (int tick = 0; tick < 90; tick++)
        {
            velocityZ += gravity * dt;
            body.Velocity = new Vector3(
                horizontalVelocity.X,
                horizontalVelocity.Y,
                velocityZ);
            ResolveResult result = engine.ResolveWithTransition(
                position,
                position + body.Velocity * dt,
                Cell,
                radius,
                radius * 2f,
                stepUpHeight: 0.30f,
                stepDownHeight: 0.04f,
                isOnGround: body.OnWalkable,
                body,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000000u);

            position = result.Position;
            body.Position = position;
            if (result.IsOnGround)
            {
                velocityZ = 0f;
                body.Velocity = new Vector3(
                    horizontalVelocity.X,
                    horizontalVelocity.Y,
                    0f);
            }
            ApplyContactResult(body, result);
            trace.Add(CaptureFrame(result, body));

            if (position.X < 0f && position.Z <= radius + 0.05f)
                break;
        }

        return new TraceRun(trace, LandedFrame: -1, LedgeStartFrame: -1);
    }

    private static TraceRun RunFlatRoofLedgeTrace(bool preparedFlat, bool includeRoof)
    {
        var fixture = BSPStepUpFixtures.FlatRoof();
        PhysicsEngine engine = BuildCollisionEngine(
            fixture,
            preparedFlat,
            0x0100E102u,
            includeGeometry: includeRoof);
        float radius = BSPStepUpFixtures.SphereRadius;
        const float dt = 1f / 30f;
        Vector3 position = new(1.55f, -0.75f, 3.6f);
        float velocityZ = 0f;
        var body = new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            State = PhysicsStateFlags.Gravity | PhysicsStateFlags.ReportCollisions,
            TransientState = TransientStateFlags.Active,
        };
        var trace = new List<TraceFrame>(72);
        int landedFrame = -1;

        for (int tick = 0; tick < 60; tick++)
        {
            velocityZ += PhysicsBody.Gravity * dt;
            body.Velocity = new Vector3(0f, 0f, velocityZ);
            ResolveResult result = engine.ResolveWithTransition(
                position,
                position + new Vector3(0f, 0f, velocityZ * dt),
                Cell,
                radius,
                radius * 2f,
                stepUpHeight: 0.30f,
                stepDownHeight: 0.04f,
                isOnGround: body.OnWalkable,
                body,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000001u);

            position = result.Position;
            body.Position = position;
            ApplyContactResult(body, result);
            trace.Add(CaptureFrame(result, body));

            if (result.InContact && result.OnWalkable)
            {
                landedFrame = trace.Count - 1;
                velocityZ = 0f;
                body.Velocity = Vector3.Zero;
                break;
            }
        }

        int ledgeStartFrame = trace.Count;
        if (landedFrame >= 0)
        {
            for (int tick = 0; tick < 12; tick++)
            {
                ResolveResult result = engine.ResolveWithTransition(
                    position,
                    position + new Vector3(0.12f, 0.08f, 0f),
                    Cell,
                    radius,
                    radius * 2f,
                    stepUpHeight: 0.30f,
                    stepDownHeight: 0.04f,
                    isOnGround: body.OnWalkable,
                    body,
                    ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                    movingEntityId: 0x01000001u);

                position = result.Position;
                body.Position = position;
                ApplyContactResult(body, result);
                trace.Add(CaptureFrame(result, body));
            }
        }

        return new TraceRun(trace, landedFrame, ledgeStartFrame);
    }

    private static WallMaintenanceTrace RunGroundedFloorWallSlide(
        bool preparedFlat,
        bool twoSpheres)
    {
        var fixture = BSPStepUpFixtures.TallWall();
        PhysicsEngine engine = BuildCollisionEngine(
            fixture,
            preparedFlat,
            0x0100E103u);
        int supportPasses = 0;
        int placementPasses = 0;
        engine.TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
        {
            if (phase == TransitionCellCollisionPhase.Environment)
            {
                if (candidate.SpherePath.StepDown
                    && actual == TransitionState.OK
                    && candidate.CollisionInfo.ContactPlaneValid
                    && candidate.CollisionInfo.ContactPlane.Normal.Z
                       >= candidate.SpherePath.WalkableAllowance)
                    supportPasses++;
                else if (candidate.SpherePath.InsertType == InsertType.Placement)
                    placementPasses++;
            }

            return actual;
        };

        float radius = BSPStepUpFixtures.SphereRadius;
        Vector3 position = new(0.5f - radius, -0.55f, 0f);
        var floor = fixture.Resolved[BSPStepUpFixtures.TallWall_FloorId];
        var body = new PhysicsBody
        {
            Position = position,
            Orientation = Quaternion.Identity,
            GroundNormal = Vector3.UnitZ,
            ContactPlaneValid = true,
            ContactPlane = floor.Plane,
            ContactPlaneCellId = Cell,
            WalkablePolygonValid = true,
            WalkablePlane = floor.Plane,
            WalkableVertices = floor.Vertices,
            WalkableUp = Vector3.UnitZ,
            TransientState = TransientStateFlags.Active
                             | TransientStateFlags.Contact
                             | TransientStateFlags.OnWalkable,
        };
        var trace = new List<TraceFrame>(10);

        for (int tick = 0; tick < 10; tick++)
        {
            body.Velocity = new Vector3(1.8f, 2.1f, 0f);
            ResolveResult result = engine.ResolveWithTransition(
                position,
                position + new Vector3(0.06f, 0.07f, 0f),
                Cell,
                radius,
                sphereHeight: twoSpheres ? 1.835f : 0f,
                stepUpHeight: 0.04f,
                stepDownHeight: 0.04f,
                isOnGround: true,
                body,
                ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0x01000002u);

            position = result.Position;
            body.Position = position;
            ApplyContactResult(body, result);
            trace.Add(CaptureFrame(result, body));
        }

        return new WallMaintenanceTrace(trace, supportPasses, placementPasses);
    }

    private static PhysicsEngine BuildCollisionEngine(
        (PhysicsBSPNode Root, Dictionary<ushort, ResolvedPolygon> Resolved) fixture,
        bool preparedFlat,
        uint gfxObjId,
        bool includeGeometry = true)
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
            SourceId = gfxObjId,
            BSP = new PhysicsBSPTree { Root = fixture.Root },
            Resolved = normalized,
            BoundingSphere = fixture.Root.BoundingSphere,
        };
        var cache = new PhysicsDataCache();
        if (includeGeometry && preparedFlat)
        {
            cache.CollisionTraversalMode = CollisionTraversalMode.Flat;
            cache.CacheGfxObj(gfxObjId, FlatCollisionAssetBuilder.FlattenGfxObj(physics));
        }
        else if (includeGeometry)
        {
            cache.RegisterGfxObjForTest(gfxObjId, physics);
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
        if (includeGeometry)
        {
            engine.ShadowObjects.Register(
                gfxObjId,
                gfxObjId,
                Vector3.Zero,
                Quaternion.Identity,
                fixture.Root.BoundingSphere.Radius,
                0f,
                0f,
                0xA9B4FFFFu,
                ShadowCollisionType.BSP,
                1f);
        }
        return engine;
    }

    private static void ApplyContactResult(PhysicsBody body, ResolveResult result)
    {
        body.TransientState &= ~(TransientStateFlags.Contact | TransientStateFlags.OnWalkable);
        if (result.InContact)
            body.TransientState |= TransientStateFlags.Contact;
        if (result.OnWalkable)
            body.TransientState |= TransientStateFlags.OnWalkable;
    }

    private static void AssertTraceParity(TraceRun expected, TraceRun actual)
    {
        Assert.Equal(expected.LandedFrame, actual.LandedFrame);
        Assert.Equal(expected.LedgeStartFrame, actual.LedgeStartFrame);
        Assert.Equal(expected.Frames.Count, actual.Frames.Count);
        for (int i = 0; i < expected.Frames.Count; i++)
        {
            Assert.Equal(expected.Frames[i].ResolveBits, actual.Frames[i].ResolveBits);
            Assert.Equal(expected.Frames[i].BodyBits, actual.Frames[i].BodyBits);
        }
    }

    private static void AssertNoLongFrozenStreak(
        IReadOnlyList<TraceFrame> trace,
        int maximumTicks)
    {
        int streak = 0;
        for (int i = 1; i < trace.Count; i++)
        {
            streak = Vector3.Distance(
                    trace[i - 1].Result.Position,
                    trace[i].Result.Position) < 0.001f
                ? streak + 1
                : 0;
            Assert.True(streak <= maximumTicks,
                $"Trace froze for {streak} ticks at {trace[i].Result.Position}.");
        }
    }

    private static TraceFrame CaptureFrame(ResolveResult result, PhysicsBody body) =>
        new(
            result,
            ResolveSignature(result),
            BodySignature(body),
            body.ContactPlaneValid,
            body.ContactPlane,
            body.WalkablePolygonValid,
            body.TransientState);

    private static string ResolveSignature(ResolveResult result)
    {
        var signature = new StringBuilder(256);
        Append(signature, result.Position);
        Append(signature, result.CellId);
        Append(signature, result.IsOnGround);
        Append(signature, result.CollisionNormalValid);
        Append(signature, result.CollisionNormal);
        Append(signature, result.Ok);
        Append(signature, result.Orientation);
        Append(signature, result.InContact);
        Append(signature, result.OnWalkable);
        Append(signature, result.ContactPlane);
        Append(signature, result.ContactPlaneCellId);
        Append(signature, result.ContactPlaneIsWater);
        return signature.ToString();
    }

    private static string BodySignature(PhysicsBody body)
    {
        var signature = new StringBuilder(512);
        Append(signature, body.Position);
        Append(signature, body.CellPosition.ObjCellId);
        Append(signature, body.CellPosition.Frame.Origin);
        Append(signature, body.CellPosition.Frame.Orientation);
        Append(signature, body.InWorld);
        Append(signature, body.Orientation);
        Append(signature, body.Velocity);
        Append(signature, body.CachedVelocity);
        Append(signature, body.FramesStationaryFall);
        Append(signature, body.Acceleration);
        Append(signature, body.Omega);
        Append(signature, body.GroundNormal);
        Append(signature, body.SlidingNormal);
        Append(signature, body.ContactPlaneValid);
        Append(signature, body.ContactPlane);
        Append(signature, body.ContactPlaneCellId);
        Append(signature, body.ContactPlaneIsWater);
        Append(signature, body.WalkablePolygonValid);
        Append(signature, body.WalkablePlane);
        if (body.WalkableVertices is null)
        {
            Append(signature, -1);
        }
        else
        {
            Append(signature, body.WalkableVertices.Length);
            foreach (Vector3 vertex in body.WalkableVertices)
                Append(signature, vertex);
        }
        Append(signature, body.WalkableUp);
        Append(signature, body.Elasticity);
        Append(signature, body.Friction);
        Append(signature, (uint)body.State);
        Append(signature, (uint)body.TransientState);
        Append(signature, BitConverter.DoubleToUInt64Bits(body.LastUpdateTime));
        Append(signature, body.IsFullyConstrained);
        Append(signature, body.LastMoveWasAutonomous);
        return signature.ToString();
    }

    private static void Append(StringBuilder target, bool value) =>
        target.Append(value ? "1|" : "0|");

    private static void Append(StringBuilder target, int value) =>
        target.Append(value).Append('|');

    private static void Append(StringBuilder target, uint value) =>
        target.Append(value.ToString("X8")).Append('|');

    private static void Append(StringBuilder target, ulong value) =>
        target.Append(value.ToString("X16")).Append('|');

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

    private static void AssertFinite(Vector3 value, string context)
    {
        Assert.True(
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z),
            $"Non-finite position in {context}: {value}.");
    }

    private sealed record TraceRun(
        List<TraceFrame> Frames,
        int LandedFrame,
        int LedgeStartFrame);

    private sealed record WallMaintenanceTrace(
        List<TraceFrame> Frames,
        int SupportPasses,
        int PlacementPasses);

    private sealed record TraceFrame(
        ResolveResult Result,
        string ResolveBits,
        string BodyBits,
        bool BodyContactPlaneValid,
        Plane BodyContactPlane,
        bool BodyWalkablePolygonValid,
        TransientStateFlags BodyTransientState);
}
