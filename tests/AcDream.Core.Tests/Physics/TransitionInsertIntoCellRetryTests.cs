using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class TransitionInsertIntoCellRetryTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = 0xA9B40001u;
    private const uint ShellGfxObj = 0x0100F001u;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedRetry_RecomputesAtomicCellPipeline_AndExceedsOuterBudget(
        bool preparedFlat)
    {
        PhysicsEngine engine = BuildEngine(preparedFlat);
        var phases = new List<TransitionCellCollisionPhase>();
        int environmentCalls = 0;
        int buildingCalls = 0;
        int objectCalls = 0;

        engine.TransitionCellCollisionTestHook = (_, phase, cellId, actual) =>
        {
            Assert.Equal(Cell, cellId);
            Assert.Equal(TransitionState.OK, actual);
            phases.Add(phase);

            switch (phase)
            {
                case TransitionCellCollisionPhase.Environment:
                    environmentCalls++;
                    return environmentCalls == 1
                        ? TransitionState.Adjusted
                        : TransitionState.OK;
                case TransitionCellCollisionPhase.Building:
                    buildingCalls++;
                    return buildingCalls == 1
                        ? TransitionState.Adjusted
                        : TransitionState.OK;
                case TransitionCellCollisionPhase.Objects:
                    objectCalls++;
                    return objectCalls == 1
                        ? TransitionState.Adjusted
                        : TransitionState.OK;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase));
            }
        };

        Vector3 current = new(10f, 10f, 5f);
        Vector3 target = current + new Vector3(0.05f, 0f, 0f);
        var body = new PhysicsBody
        {
            Position = current,
            Orientation = Quaternion.Identity,
            TransientState = TransientStateFlags.Active,
        };

        ResolveResult result = engine.ResolveWithTransition(
            current,
            target,
            Cell,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f,
            stepUpHeight: 0.6f,
            stepDownHeight: 1.5f,
            isOnGround: false,
            body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x000F4243u);

        Assert.True(result.Ok);
        Assert.Equal(target, result.Position);

        TransitionCellCollisionPhase[] expected =
        [
            TransitionCellCollisionPhase.Environment,
            TransitionCellCollisionPhase.Environment,
            TransitionCellCollisionPhase.Building,
            TransitionCellCollisionPhase.Environment,
            TransitionCellCollisionPhase.Building,
            TransitionCellCollisionPhase.Objects,
            TransitionCellCollisionPhase.Environment,
            TransitionCellCollisionPhase.Building,
            TransitionCellCollisionPhase.Objects,
        ];
        Assert.Equal(expected, phases);
        Assert.Equal(4, environmentCalls);
        Assert.Equal(3, buildingCalls);
        Assert.Equal(2, objectCalls);
        Assert.True(
            environmentCalls > 3,
            "The fixture must require more complete cell passes than the "
            + "outer N=3 budget while remaining inside retail's N×N budget.");
    }

    [Fact]
    public void NestedRetry_AlwaysAdjusted_ExhaustsExactNByNBudget()
    {
        PhysicsEngine engine = BuildEngine(preparedFlat: false);
        int calls = 0;
        engine.TransitionCellCollisionTestHook =
            (_, phase, cellId, actual) =>
            {
                Assert.Equal(TransitionCellCollisionPhase.Environment, phase);
                Assert.Equal(Cell, cellId);
                Assert.Equal(TransitionState.OK, actual);
                calls++;
                return TransitionState.Adjusted;
            };

        Vector3 current = new(10f, 10f, 5f);
        Vector3 target = current + new Vector3(0.05f, 0f, 0f);
        Transition transition = BSPStepUpFixtures.MakeAirborneTransition(
            current,
            target,
            Cell);
        transition.SpherePath.SetCheckPos(target, Cell);
        TransitionState result = transition.TransitionalInsertForTest(3, engine);

        Assert.Equal(TransitionState.Adjusted, result);
        Assert.Equal(target, transition.SpherePath.CheckPos);
        Assert.Equal(9, calls);
    }

    [Fact]
    public void NestedRetry_SlidClearsInnerContact_ThenOuterNegPoly()
    {
        PhysicsEngine engine = BuildEngine(preparedFlat: false);
        int environmentCalls = 0;
        var contactPlane = new Plane(Vector3.UnitZ, -5f);

        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, actual) =>
            {
                if (phase != TransitionCellCollisionPhase.Environment)
                    return actual;

                environmentCalls++;
                if (environmentCalls <= 3)
                {
                    if (environmentCalls > 1)
                    {
                        Assert.False(transition.CollisionInfo.ContactPlaneValid);
                        Assert.False(transition.CollisionInfo.ContactPlaneIsWater);
                        Assert.True(transition.SpherePath.NegPolyHit);
                    }

                    transition.CollisionInfo.SetContactPlane(
                        contactPlane,
                        Cell,
                        isWater: true);
                    transition.SpherePath.NegPolyHit = true;
                    return TransitionState.Slid;
                }

                Assert.False(transition.CollisionInfo.ContactPlaneValid);
                Assert.False(transition.CollisionInfo.ContactPlaneIsWater);
                Assert.False(transition.SpherePath.NegPolyHit);
                return actual;
            };

        Vector3 current = new(10f, 10f, 5f);
        ResolveResult result = ResolveAirborne(
            engine,
            current,
            current + new Vector3(0.05f, 0f, 0f),
            Cell);

        Assert.True(result.Ok);
        Assert.Equal(4, environmentCalls);
    }

    [Fact]
    public void NestedRetry_FixesPrimaryCellUntilNextOuterAttempt()
    {
        PhysicsEngine engine = BuildEngine(preparedFlat: false);
        var cells = new List<uint>();
        var phases = new List<TransitionCellCollisionPhase>();
        int environmentCalls = 0;
        const uint nextOuterCell = 0xA9B40002u;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, cellId, actual) =>
            {
                phases.Add(phase);
                cells.Add(cellId);

                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    environmentCalls++;
                    if (environmentCalls == 1)
                    {
                        transition.SpherePath.SetCheckPos(
                            transition.SpherePath.CheckPos,
                            nextOuterCell);
                    }

                    if (environmentCalls <= 3)
                        return TransitionState.Adjusted;
                }

                return actual;
            };

        Vector3 current = new(10f, 10f, 5f);
        Vector3 target = current + new Vector3(0.05f, 0f, 0f);
        ResolveResult result = ResolveAirborne(
            engine,
            current,
            target,
            Cell);

        Assert.True(result.Ok);
        Assert.Equal(target, result.Position);
        Assert.Equal(
            new[]
            {
                TransitionCellCollisionPhase.Environment,
                TransitionCellCollisionPhase.Environment,
                TransitionCellCollisionPhase.Environment,
                TransitionCellCollisionPhase.Environment,
                TransitionCellCollisionPhase.Building,
                TransitionCellCollisionPhase.Objects,
            },
            phases);
        Assert.Equal(
            new[]
            {
                Cell,
                Cell,
                Cell,
                nextOuterCell,
                nextOuterCell,
                nextOuterCell,
            },
            cells);
    }

    private static ResolveResult ResolveAirborne(
        PhysicsEngine engine,
        Vector3 current,
        Vector3 target,
        uint cellId)
    {
        var body = new PhysicsBody
        {
            Position = current,
            Orientation = Quaternion.Identity,
            TransientState = TransientStateFlags.Active,
        };
        return engine.ResolveWithTransition(
            current,
            target,
            cellId,
            sphereRadius: 0.48f,
            sphereHeight: 1.835f,
            stepUpHeight: 0.6f,
            stepDownHeight: 1.5f,
            isOnGround: false,
            body,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0x000F4243u);
    }

    private static PhysicsEngine BuildEngine(bool preparedFlat)
    {
        var (root, resolved) = BSPStepUpFixtures.FlatRoof();
        var normalized = new Dictionary<ushort, ResolvedPolygon>(resolved.Count);
        foreach ((ushort id, ResolvedPolygon polygon) in resolved)
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
            SourceId = ShellGfxObj,
            BSP = new PhysicsBSPTree { Root = root },
            Resolved = normalized,
            BoundingSphere = root.BoundingSphere,
        };

        var cache = new PhysicsDataCache();
        if (preparedFlat)
        {
            cache.CollisionTraversalMode = CollisionTraversalMode.Flat;
            cache.CacheGfxObj(
                ShellGfxObj,
                FlatCollisionAssetBuilder.FlattenGfxObj(physics));
        }
        else
        {
            cache.RegisterGfxObjForTest(ShellGfxObj, physics);
        }

        cache.CacheBuilding(
            Cell,
            Array.Empty<BldPortalInfo>(),
            Matrix4x4.CreateTranslation(100f, 100f, 100f),
            ShellGfxObj);

        var engine = new PhysicsEngine { DataCache = cache };
        var heights = new byte[81];
        var heightTable = new float[256];
        Array.Fill(heightTable, -1000f);
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            0f,
            0f);
        return engine;
    }

}
