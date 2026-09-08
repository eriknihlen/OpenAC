using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class ProjectilePhysicsStepperTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = 0xA9B40001u;
    private const uint ProjectileId = 0x7000u;
    private static readonly ProjectileCollisionSphere ArrowSphere =
        new(Vector3.Zero, 0.10f);

    [Fact]
    public void CollisionSphere_ScalesOffsetAndRadius_WithoutCenteringForceBolt()
    {
        var sphere = new ProjectileCollisionSphere(
            new Vector3(0f, -0.165f, 0.1045f),
            0.102f,
            2f);

        Assert.Equal(new Vector3(0f, -0.33f, 0.209f), sphere.LocalOrigin);
        Assert.Equal(0.204f, sphere.Radius, 5);
        Assert.True(sphere.IsValid);
    }

    [Fact]
    public void CollisionSphere_RejectsInvalidComputedShape()
    {
        Assert.False(new ProjectileCollisionSphere(
            Vector3.Zero, float.MaxValue, 2f).IsValid);
        Assert.False(new ProjectileCollisionSphere(
            new Vector3(float.MaxValue, 0f, 0f), 0.1f, 2f).IsValid);
        Assert.False(new ProjectileCollisionSphere(
            Vector3.Zero, 0.0003f, 0.5f).IsValid);
        Assert.False(new ProjectileCollisionSphere(
            Vector3.Zero, 0.1f, -1f).IsValid);
        Assert.False(new ProjectileCollisionSphere(
            Vector3.One, -0.1f, -1f).IsValid);
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    [InlineData(1f, 2f, 3f)]
    public void SetVectorHeading_MapsLocalForwardToFull3dDirection(float x, float y, float z)
    {
        Vector3 direction = Vector3.Normalize(new Vector3(x, y, z));
        Quaternion orientation = RetailFrameMath.SetVectorHeading(
            Quaternion.Identity,
            direction);

        Vector3 actual = Vector3.Transform(Vector3.UnitY, orientation);
        AssertVectorNear(direction, actual, 0.0001f);
    }

    [Fact]
    public void SetVectorHeading_ZeroDirection_PreservesOrientation()
    {
        Quaternion original = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        Assert.Equal(original, RetailFrameMath.SetVectorHeading(original, Vector3.Zero));
    }

    [Theory]
    [InlineData(0.0001f, 0f, 1f)]
    [InlineData(0.000001f, -0.000002f, -1f)]
    public void SetVectorHeading_NearlyVertical_PreservesNonzeroCompass(
        float x, float y, float z)
    {
        Vector3 expected = Vector3.Normalize(new Vector3(x, y, z));
        Quaternion orientation = RetailFrameMath.SetVectorHeading(
            Quaternion.Identity,
            expected);

        AssertVectorNear(
            expected,
            Vector3.Transform(Vector3.UnitY, orientation),
            0.000001f);
    }

    [Fact]
    public void Advance_ClampsVelocityToFiftyBeforeIntegrating()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), new Vector3(0f, 100f, 0f));

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.True(result.Simulated);
        Assert.Equal(50f, body.Velocity.Length(), 4);
        Assert.Equal(12f, body.Position.Y, 3); // 10 + 50 * 0.04
        Assert.Equal(50f, body.CachedVelocity.Y, 3);
    }

    [Fact]
    public void SplitQuantum_HoldsBeginFrameAcrossHookSlotThenCommitsCandidate()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(
            new Vector3(12f, 10f, 2f),
            new Vector3(10f, 0f, 0f));
        var stepper = new ProjectilePhysicsStepper(engine);
        Vector3 begin = body.Position;

        ProjectileQuantumPreparation preparation = stepper.BeginQuantum(
            body,
            0.1f,
            Cell,
            ArrowSphere);

        Assert.True(preparation.Simulated);
        Assert.True(preparation.RequiresTransition);
        Assert.Equal(begin, body.Position);
        Assert.Equal(begin + Vector3.UnitX, preparation.CandidatePosition);

        ProjectileAdvanceResult result = stepper.CompleteQuantum(
            body,
            preparation,
            ArrowSphere,
            ProjectileId);

        Assert.True(result.Simulated);
        Assert.InRange(
            Vector3.Distance(begin + Vector3.UnitX, body.Position),
            0f,
            0.00001f);
    }

    [Fact]
    public void Advance_GravityUsesFinalPhysicsState_NotParsedAcceleration()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(
            new Vector3(12f, 10f, 10f),
            new Vector3(0f, 10f, 0f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Gravity);
        body.Acceleration = new Vector3(100f, 200f, 300f); // wire value must not drive runtime

        new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(12f, body.Position.X, 4);
        Assert.Equal(10.4f, body.Position.Y, 4);
        Assert.Equal(10f + 0.5f * PhysicsBody.Gravity * 0.04f * 0.04f,
            body.Position.Z, 4);
        Assert.Equal(PhysicsBody.Gravity * 0.04f, body.Velocity.Z, 4);
    }

    [Fact]
    public void Advance_NoGravity_ClearsStaleAcceleration()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 10f), new Vector3(0f, 10f, 0f));
        body.Acceleration = new Vector3(100f, 200f, 300f);

        new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(Vector3.Zero, body.Acceleration);
        Assert.Equal(10f, body.Position.Z, 4);
    }

    [Theory]
    [InlineData(0f, 10f, 0f)]
    [InlineData(10f, 0f, 0f)]
    [InlineData(0f, 0f, 10f)]
    [InlineData(3f, 4f, 5f)]
    public void Advance_AlignPathUsesActualThreeDimensionalDisplacement(float x, float y, float z)
    {
        var (engine, _) = BuildEmptyEngine();
        Vector3 velocity = new(x, y, z);
        var body = MakeBody(
            new Vector3(12f, 10f, 10f),
            velocity,
            PhysicsStateFlags.Missile | PhysicsStateFlags.AlignPath);

        new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Vector3 heading = Vector3.Transform(Vector3.UnitY, body.Orientation);
        AssertVectorNear(Vector3.Normalize(velocity), heading, 0.0002f);
    }

    [Fact]
    public void UpdatePhysicsInternal_AngularVelocityPremultipliesInWorldSpace()
    {
        var body = new PhysicsBody
        {
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f),
            Omega = new Vector3(0f, 0f, 2f),
        };
        Quaternion worldDelta = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f);
        Quaternion expected = Quaternion.Normalize(worldDelta * body.Orientation);

        body.UpdatePhysicsInternal(0.1f);

        AssertQuaternionEquivalent(expected, body.Orientation, 0.0001f);
    }

    [Fact]
    public void Advance_RotatingProjectileWithoutAlignPathKeepsOmegaSpin()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 10f), new Vector3(0f, 2f, 0f));
        body.Omega = new Vector3(0f, 0f, 4f);

        new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Quaternion expected = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.16f);
        AssertQuaternionEquivalent(expected, body.Orientation, 0.0001f);
    }

    [Fact]
    public void Advance_ExclusionsClearActiveAndDoNotMove()
    {
        var (engine, _) = BuildEmptyEngine();
        var stepper = new ProjectilePhysicsStepper(engine);

        var cases = new[]
        {
            (body: MakeBody(new(12f, 10f, 2f), Vector3.UnitY), parented: true),
            (body: MakeBody(new(12f, 10f, 2f), Vector3.UnitY,
                PhysicsStateFlags.Missile | PhysicsStateFlags.Frozen), parented: false),
            (body: MakeBody(new(12f, 10f, 2f), Vector3.UnitY,
                PhysicsStateFlags.Missile | PhysicsStateFlags.Static), parented: false),
        };
        cases[2].body.InWorld = true;

        foreach (var item in cases)
        {
            Vector3 before = item.body.Position;
            var result = stepper.Advance(
                item.body, 0.04, Cell, ArrowSphere, ProjectileId,
                isParented: item.parented);
            Assert.False(result.Simulated);
            Assert.False(item.body.IsActive);
            Assert.Equal(before, item.body.Position);
        }

        var outOfWorld = MakeBody(new(12f, 10f, 2f), Vector3.UnitY);
        outOfWorld.InWorld = false;
        Assert.False(stepper.Advance(
            outOfWorld, 0.04, Cell, ArrowSphere, ProjectileId).Simulated);
        Assert.False(outOfWorld.IsActive);
    }

    [Fact]
    public void Advance_AccumulatesSmallRemainderUntilRetailMinimumQuantum()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);
        var stepper = new ProjectilePhysicsStepper(engine);

        var first = stepper.Advance(body, 0.02, Cell, ArrowSphere, ProjectileId);
        Assert.False(first.Simulated);
        Assert.Equal(0d, body.LastUpdateTime, 8);

        var second = stepper.Advance(body, 0.04, Cell, ArrowSphere, ProjectileId);
        Assert.True(second.Simulated);
        Assert.Equal(10.04f, body.Position.Y, 4);
        Assert.Equal(0.04d, body.LastUpdateTime, 8);
    }

    [Fact]
    public void Advance_ExactMinimumQuantum_RemainsAccumulated()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, PhysicsBody.MinQuantum, Cell, ArrowSphere, ProjectileId);

        Assert.False(result.Simulated);
        Assert.Equal(0d, body.LastUpdateTime, 8);
        Assert.Equal(10f, body.Position.Y);
    }

    [Fact]
    public void Advance_ExactMaximumQuantum_UsesOneQuantum()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, PhysicsBody.MaxQuantum, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(1, result.QuantumCount);
        Assert.Equal(10.2f, body.Position.Y, 4);
        Assert.Equal((double)PhysicsBody.MaxQuantum, body.LastUpdateTime, 7);
    }

    [Fact]
    public void Advance_ExactHugeQuantum_IsStillSimulated()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, PhysicsBody.HugeQuantum, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(10, result.QuantumCount);
        Assert.Equal(12f, body.Position.Y, 3);
        Assert.Equal((double)PhysicsBody.HugeQuantum, body.LastUpdateTime, 7);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Advance_NonFiniteClock_IsRejectedWithoutMutation(double currentTime)
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);
        Vector3 before = body.Position;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProjectilePhysicsStepper(engine).Advance(
                body, currentTime, Cell, ArrowSphere, ProjectileId));

        Assert.Equal(before, body.Position);
        Assert.Equal(0d, body.LastUpdateTime);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Advance_NonFinitePriorClock_IsRejectedWithoutMutation(double priorTime)
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);
        body.LastUpdateTime = priorTime;
        Vector3 before = body.Position;

        Assert.Throws<InvalidOperationException>(() =>
            new ProjectilePhysicsStepper(engine).Advance(
                body, 1d, Cell, ArrowSphere, ProjectileId));

        Assert.Equal(before, body.Position);
        Assert.Equal(priorTime, body.LastUpdateTime);
    }

    [Fact]
    public void Advance_CatchupUsesPointTwoSecondQuantaAndRetainsNoLargeRemainder()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), new Vector3(0f, 50f, 0f));

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.45, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(3, result.QuantumCount); // 0.2 + 0.2 + 0.05
        Assert.Equal(32.5f, body.Position.Y, 3);
        Assert.Equal(0.45d, body.LastUpdateTime, 8);
    }

    [Fact]
    public void Advance_GapAboveTwoSecondsIsDiscarded()
    {
        var (engine, _) = BuildEmptyEngine();
        var body = MakeBody(new Vector3(12f, 10f, 2f), Vector3.UnitY);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 2.01, Cell, ArrowSphere, ProjectileId);

        Assert.False(result.Simulated);
        Assert.Equal(new Vector3(12f, 10f, 2f), body.Position);
        Assert.Equal(2.01d, body.LastUpdateTime, 8);
    }

    [Fact]
    public void Sweep_AtMaximumSpeed_DoesNotTunnelThroughThinSphere()
    {
        var (engine, _) = BuildEmptyEngine();
        RegisterSphere(engine, 0x8100u, new Vector3(12f, 11f, 2f), 0.01f);
        var body = MakeBody(
            new Vector3(12f, 10f, 2f),
            new Vector3(0f, 50f, 0f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Inelastic);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.True(result.CollisionNormalValid);
        Assert.True(body.Position.Y < 11.1f, $"Projectile tunneled to Y={body.Position.Y}");
        Assert.Equal(Vector3.Zero, body.Velocity);
    }

    [Fact]
    public void Sweep_ElasticProjectileReflectsFromObjectSurface()
    {
        var (engine, _) = BuildEmptyEngine();
        RegisterSphere(engine, 0x8101u, new Vector3(12f, 11f, 2f), 0.01f);
        var body = MakeBody(
            new Vector3(12f, 10f, 2f),
            new Vector3(0f, 50f, 0f));
        body.Elasticity = 0.5f;

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.True(result.CollisionNormalValid);
        Assert.Equal(-25f, body.Velocity.Y, 2);
    }

    [Fact]
    public void Sweep_DownwardProjectileCollidesWithTerrainFloor()
    {
        var engine = BuildTerrainEngine(0f);
        var body = MakeBody(
            new Vector3(12f, 10f, 0.3f),
            new Vector3(0f, 0f, -10f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Inelastic);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.True(result.CollisionNormalValid);
        Assert.True(body.Position.Z >= 0.09f);
        Assert.Equal(Vector3.Zero, body.Velocity);
        Assert.True(body.InContact);
        Assert.True(body.OnWalkable);

        Vector3 settled = body.Position;
        var settleTick = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.08, Cell, ArrowSphere, ProjectileId);
        Assert.True(settleTick.Simulated);
        Assert.Equal(settled, body.Position);
        Assert.False(body.IsActive);

        var inactiveTick = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.12, Cell, ArrowSphere, ProjectileId);
        Assert.False(inactiveTick.Simulated);
        Assert.Equal(settled, body.Position);
    }

    [Fact]
    public void Sweep_FailedTransition_KeepsCandidateFrameAndCurrentCell()
    {
        var engine = BuildBoundaryEngine();
        var body = MakeBody(
            new Vector3(10f, 191f, 50f), new Vector3(0f, 50f, 0f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Inelastic,
            cellId: 0xA9B40008u,
            cellLocal: new Vector3(10f, 191f, 50f));
        body.SlidingNormal = -Vector3.UnitY;
        body.TransientState |=
            TransientStateFlags.Sliding | TransientStateFlags.StationaryStop;
        body.FramesStationaryFall = 2;
        body.ContactPlaneValid = true;
        body.ContactPlane = new Plane(Vector3.UnitZ, -50f);
        body.ContactPlaneCellId = 0xA9B40008u;
        body.ContactPlaneIsWater = true;
        body.WalkablePolygonValid = true;
        body.WalkablePlane = new Plane(Vector3.UnitZ, -50f);
        body.WalkableVertices =
        [
            new Vector3(0f, 0f, 50f),
            new Vector3(1f, 0f, 50f),
            new Vector3(0f, 1f, 50f),
        ];
        body.WalkableUp = Vector3.UnitZ;
        Plane priorContactPlane = body.ContactPlane;
        Vector3[] priorWalkableVertices = (Vector3[])body.WalkableVertices.Clone();
        TransientStateFlags priorTransient = body.TransientState;

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, 0xA9B40008u, ArrowSphere, ProjectileId);

        Assert.False(result.TransitionOk);
        Assert.Equal(0xA9B40008u, result.CellId);
        Assert.Equal(193f, body.Position.Y, 3);
        Assert.Equal(0xA9B40008u, body.CellPosition.ObjCellId);
        Assert.Equal(193f, body.CellPosition.Frame.Origin.Y, 3);
        Assert.Equal(Vector3.Zero, body.CachedVelocity);
        Assert.Equal(50f, body.Velocity.Y);
        Assert.True(body.ContactPlaneValid);
        Assert.Equal(priorContactPlane, body.ContactPlane);
        Assert.Equal(0xA9B40008u, body.ContactPlaneCellId);
        Assert.True(body.ContactPlaneIsWater);
        Assert.True(body.WalkablePolygonValid);
        Assert.Equal(priorWalkableVertices, body.WalkableVertices);
        Assert.Equal(Vector3.UnitZ, body.WalkableUp);
        Assert.Equal(2, body.FramesStationaryFall);
        Assert.Equal(priorTransient, body.TransientState);
    }

    [Fact]
    public void Sweep_SelfShadowIsExcluded()
    {
        var (engine, _) = BuildEmptyEngine();
        RegisterSphere(engine, ProjectileId, new Vector3(12f, 11f, 2f), 0.01f);
        var body = MakeBody(new(12f, 10f, 2f), new(0f, 50f, 0f));

        new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(12f, body.Position.Y, 3);
    }

    [Fact]
    public void Sweep_OtherMissileIsIgnored()
    {
        var (engine, _) = BuildEmptyEngine();
        RegisterSphere(
            engine, 0x8200u, new Vector3(12f, 11f, 2f), 0.01f,
            (uint)PhysicsStateFlags.Missile,
            EntityCollisionFlags.HasWeenie);
        var body = MakeBody(new(12f, 10f, 2f), new(0f, 50f, 0f));

        new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.Equal(12f, body.Position.Y, 3);
    }

    [Fact]
    public void Sweep_EtherealWeenieIsIgnored()
    {
        var (ignoreEngine, _) = BuildEmptyEngine();
        RegisterSphere(
            ignoreEngine, 0x8300u, new Vector3(12f, 11f, 2f), 0.01f,
            (uint)PhysicsStateFlags.Ethereal,
            EntityCollisionFlags.HasWeenie);
        var ignoredBody = MakeBody(new(12f, 10f, 2f), new(0f, 50f, 0f));
        new ProjectilePhysicsStepper(ignoreEngine).Advance(
            ignoredBody, 0.04, Cell, ArrowSphere, ProjectileId);
        Assert.Equal(12f, ignoredBody.Position.Y, 3);
    }

    [Fact]
    public void Sweep_WithKnownTarget_IgnoresOtherCreatureAndHitsDesignatedCreature()
    {
        var (engine, _) = BuildEmptyEngine();
        const uint targetId = 0x8401u;
        var creature = EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature;
        RegisterSphere(engine, 0x8400u, new(12f, 10.8f, 2f), 0.05f, 0u, creature);
        RegisterSphere(engine, targetId, new(12f, 11.5f, 2f), 0.05f, 0u, creature);
        var body = MakeBody(
            new(12f, 10f, 2f), new(0f, 50f, 0f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Inelastic);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId, targetId);

        Assert.True(result.CollisionNormalValid);
        Assert.True(body.Position.Y > 10.8f, "The non-designated creature blocked the missile");
        Assert.True(body.Position.Y < 11.6f, "The designated creature was skipped");
    }

    [Fact]
    public void Sweep_UnknownTargetCollidesWithCreature()
    {
        var (engine, _) = BuildEmptyEngine();
        RegisterSphere(
            engine, 0x8500u, new(12f, 11f, 2f), 0.05f, 0u,
            EntityCollisionFlags.HasWeenie | EntityCollisionFlags.IsCreature);
        var body = MakeBody(
            new(12f, 10f, 2f), new(0f, 50f, 0f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Inelastic);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.True(result.CollisionNormalValid);
    }

    [Fact]
    public void Sweep_ThinBspWallStopsInelasticProjectile()
    {
        var (engine, cache) = BuildEmptyEngine();
        RegisterThinBspWall(engine, cache, y: 11f);
        var body = MakeBody(
            new(12f, 10f, 2f), new(0f, 50f, 0f),
            PhysicsStateFlags.Missile | PhysicsStateFlags.Inelastic);

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.04, Cell, ArrowSphere, ProjectileId);

        Assert.True(result.CollisionNormalValid);
        Assert.True(body.Position.Y < 11.1f);
        Assert.Equal(Vector3.Zero, body.Velocity);
    }

    [Theory]
    [InlineData(191f, 10f,  40f,   0f, 0xA9B40039u, 0xAAB40001u)]
    [InlineData(  1f, 10f, -40f,   0f, 0xA9B40001u, 0xA8B40039u)]
    [InlineData( 10f,191f,   0f,  40f, 0xA9B40008u, 0xA9B50001u)]
    [InlineData( 10f,  1f,   0f, -40f, 0xA9B40001u, 0xA9B30008u)]
    public void Sweep_CrossesLoadedLandblockBoundaryInEveryDirection(
        float startX, float startY,
        float velocityX, float velocityY,
        uint startCell, uint expectedCell)
    {
        var engine = BuildBoundaryEngine();
        var body = MakeBody(
            new(startX, startY, 50f), new(velocityX, velocityY, 0f),
            PhysicsStateFlags.Missile,
            cellId: startCell,
            cellLocal: new(startX, startY, 50f));

        var result = new ProjectilePhysicsStepper(engine).Advance(
            body, 0.05, startCell,
            new ProjectileCollisionSphere(Vector3.Zero, 0.5f),
            ProjectileId);

        Assert.Equal(expectedCell, result.CellId);
        Assert.Equal(startX + (velocityX * 0.05f), body.Position.X, 3);
        Assert.Equal(startY + (velocityY * 0.05f), body.Position.Y, 3);
    }

    private static PhysicsBody MakeBody(
        Vector3 position,
        Vector3 velocity,
        PhysicsStateFlags state = PhysicsStateFlags.Missile,
        uint cellId = Cell,
        Vector3? cellLocal = null)
    {
        var body = new PhysicsBody
        {
            State = state,
            Position = position,
            Orientation = Quaternion.Identity,
            LastUpdateTime = 0d,
        };
        body.SnapToCell(cellId, position, cellLocal ?? position);
        body.Velocity = velocity;
        body.TransientState = TransientStateFlags.Active;
        return body;
    }

    private static (PhysicsEngine Engine, PhysicsDataCache Cache) BuildEmptyEngine()
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        RegisterTerrain(engine, -1000f);
        return (engine, cache);
    }

    private static PhysicsEngine BuildTerrainEngine(float height)
    {
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        RegisterTerrain(engine, height);
        return engine;
    }

    private static void RegisterTerrain(PhysicsEngine engine, float height)
    {
        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = height;
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(heights, heightTable),
            System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(),
            0f,
            0f);
    }

    private static PhysicsEngine BuildBoundaryEngine()
    {
        static TerrainSurface EmptyTerrain()
        {
            var heights = new byte[81];
            var table = new float[256];
            for (int i = 0; i < table.Length; i++) table[i] = -1000f;
            return new TerrainSurface(heights, table);
        }

        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.AddLandblock(0xA9B4FFFFu, EmptyTerrain(), System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(), 0f, 0f);
        engine.AddLandblock(0xA8B4FFFFu, EmptyTerrain(), System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(), -192f, 0f);
        engine.AddLandblock(0xAAB4FFFFu, EmptyTerrain(), System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(), 192f, 0f);
        engine.AddLandblock(0xA9B3FFFFu, EmptyTerrain(), System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(), 0f, -192f);
        engine.AddLandblock(0xA9B5FFFFu, EmptyTerrain(), System.Array.Empty<CellSurface>(),
            System.Array.Empty<PortalPlane>(), 0f, 192f);
        return engine;
    }

    private static void RegisterSphere(
        PhysicsEngine engine,
        uint id,
        Vector3 position,
        float radius,
        uint state = (uint)PhysicsStateFlags.Static,
        EntityCollisionFlags flags = EntityCollisionFlags.None)
    {
        engine.ShadowObjects.Register(
            id,
            0u,
            position,
            Quaternion.Identity,
            radius,
            0f,
            0f,
            Landblock,
            ShadowCollisionType.Sphere,
            state: state,
            flags: flags,
            isStatic: (state & (uint)PhysicsStateFlags.Static) != 0);
    }

    private static void RegisterThinBspWall(
        PhysicsEngine engine,
        PhysicsDataCache cache,
        float y)
    {
        const uint gfxId = 0x0100F001u;
        const uint entityId = 0x8600u;
        var vertices = new[]
        {
            new Vector3(-2f, 0f, -2f),
            new Vector3( 2f, 0f, -2f),
            new Vector3( 2f, 0f,  2f),
            new Vector3(-2f, 0f,  2f),
        };
        var polygon = new ResolvedPolygon
        {
            Vertices = vertices,
            Plane = new Plane(new Vector3(0f, -1f, 0f), 0f),
            NumPoints = 4,
            SidesType = CullMode.None,
        };
        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 4f },
        };
        leaf.Polygons.Add(0);
        cache.RegisterGfxObjForTest(gfxId, new GfxObjPhysics
        {
            BSP = new PhysicsBSPTree { Root = leaf },
            PhysicsPolygons = new Dictionary<ushort, Polygon>(),
            Vertices = new VertexArray(),
            Resolved = new Dictionary<ushort, ResolvedPolygon> { [0] = polygon },
            BoundingSphere = new Sphere { Origin = Vector3.Zero, Radius = 4f },
        });
        engine.ShadowObjects.Register(
            entityId,
            gfxId,
            new Vector3(12f, y, 2f),
            Quaternion.Identity,
            4f,
            0f,
            0f,
            Landblock,
            ShadowCollisionType.BSP,
            state: (uint)(PhysicsStateFlags.Static | PhysicsStateFlags.HasPhysicsBsp),
            isStatic: true);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float epsilon)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, epsilon);
    }

    private static void AssertQuaternionEquivalent(
        Quaternion expected,
        Quaternion actual,
        float epsilon)
    {
        float dot = MathF.Abs(Quaternion.Dot(expected, actual));
        Assert.InRange(dot, 1f - epsilon, 1f + epsilon);
    }
}
