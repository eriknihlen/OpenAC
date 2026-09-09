using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics;

public sealed class PhysicsSetPositionTests
{
    private const uint Landblock = 0xA9B40000u;
    private const uint Cell = Landblock | 0x0001u;

    [Fact]
    public void SetPositionErrorValues_MatchRetailHeader()
    {
        Assert.Equal(0, (int)PhysicsSetPositionError.Ok);
        Assert.Equal(1, (int)PhysicsSetPositionError.GeneralFailure);
        Assert.Equal(2, (int)PhysicsSetPositionError.NoValidPosition);
        Assert.Equal(3, (int)PhysicsSetPositionError.NoCell);
        Assert.Equal(4, (int)PhysicsSetPositionError.Collided);
        Assert.Equal(0x100, (int)PhysicsSetPositionError.InvalidArguments);
    }

    [Fact]
    public void MissingOutdoorCell_AdjustsFrameThenDefersWithOk()
    {
        var engine = new PhysicsEngine();
        var local = new Vector3(193f, 12f, 7f);
        uint expectedCell = Cell;
        Vector3 expectedLocal = local;
        Assert.True(AcDream.Core.Physics.LandDefs.AdjustToOutside(
            ref expectedCell,
            ref expectedLocal));

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(Cell, local, position: new Vector3(193f, 12f, 7f)));

        Assert.Equal(PhysicsSetPositionError.Ok, result.Error);
        Assert.Equal(
            PhysicsResidenceDisposition.DeferredCell,
            result.Residence);
        Assert.Equal(expectedCell, result.CellId);
        Assert.Equal(expectedLocal, result.CellLocalPosition);
        Assert.Equal(new Vector3(193f, 12f, 7f), result.Position);
        Assert.Equal(
            new[] { Cell, expectedCell }.Distinct(),
            result.QueriedCellIds);
    }

    [Fact]
    public void ResidentCrossLandblockPlacement_RebasesLocalFrameAndPreservesWorldPosition()
    {
        PhysicsEngine engine = FlatEngine();
        const uint destinationLandblock = 0xAAB40000u;
        AddFlatLandblock(engine, destinationLandblock, worldOffsetX: 192f);
        var world = new Vector3(193f, 12f, 7f);

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(Cell, world, world) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
            });

        Assert.True(result.IsCommitted);
        Assert.Equal(world, result.Position);
        Assert.Equal(destinationLandblock | 0x0001u, result.CellId);
        Assert.Equal(new Vector3(1f, 12f, 7f), result.CellLocalPosition);
    }

    [Fact]
    public void OutdoorMapEdgeFailure_ZeroesCellAndDefersExactFrame()
    {
        var engine = new PhysicsEngine();
        const uint southWestCell = 0x00000001u;
        var local = new Vector3(-1f, 12f, 7f);

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(southWestCell, local, local));

        Assert.True(result.IsDeferred);
        Assert.Equal(0u, result.CellId);
        Assert.Equal(local, result.CellLocalPosition);
        Assert.Equal(local, result.Position);
        Assert.Equal(new[] { southWestCell, 0u }, result.QueriedCellIds);
    }

    [Fact]
    public void QueryFootprintIncludesAdjustPositionVisibleChildProbes()
    {
        const uint start = Landblock | 0x0101u;
        const uint sibling = Landblock | 0x0102u;
        var engine = new PhysicsEngine { DataCache = new PhysicsDataCache() };
        engine.DataCache.RegisterCellStructForTest(
            start,
            ContainmentCell(
                new Plane(new Vector3(0f, -1f, 0f), 3f),
                [sibling]));
        engine.DataCache.RegisterCellStructForTest(
            sibling,
            ContainmentCell(
                new Plane(new Vector3(0f, 1f, 0f), -7f),
                []));

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                start,
                new Vector3(0f, 8f, 1f),
                new Vector3(0f, 8f, 1f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
            });

        Assert.Contains(start, result.QueriedCellIds);
        Assert.Contains(sibling, result.QueriedCellIds);
        Assert.True(
            result.QueriedCellIds.IndexOf(start)
                < result.QueriedCellIds.IndexOf(sibling));
    }

    [Fact]
    public void LateralVisibleChildRecoveryRecordsRejectedAndWinningSiblingsUnionOnly()
    {
        const uint start = Landblock | 0x0101u;
        const uint rejected = Landblock | 0x0102u;
        const uint winner = Landblock | 0x0103u;
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(
            start,
            ContainmentCell(
                new Plane(new Vector3(0f, -1f, 0f), 3f),
                [rejected, winner]));
        cache.RegisterCellStructForTest(
            rejected,
            ContainmentCell(
                new Plane(new Vector3(0f, 1f, 0f), -20f),
                []));
        cache.RegisterCellStructForTest(
            winner,
            ContainmentCell(
                new Plane(new Vector3(0f, 1f, 0f), -7f),
                []));
        var candidates = new CellArray();
        var queryFootprint = new CellArray();
        candidates.UnionTarget = queryFootprint;
        var spheres = new[]
        {
            new Sphere
            {
                Origin = new Vector3(0f, 8f, 1f),
                Radius = 0.48f,
            },
        };

        uint containing = CellTransit.FindCellSet(
            cache,
            spheres,
            spheres.Length,
            start,
            candidates);

        Assert.Equal(winner, containing);
        Assert.Equal(new[] { start }, candidates.OrderedIds);
        Assert.Equal(
            new[] { start, rejected, winner },
            queryFootprint.OrderedIds);
    }

    [Fact]
    public void RejectedPortalContainmentProbeIsRecordedUnionOnly()
    {
        const uint start = Landblock | 0x0101u;
        const uint rejected = Landblock | 0x0102u;
        const ushort portalPolygonId = 10;
        var portalPolygon = new ResolvedPolygon
        {
            Id = portalPolygonId,
            Vertices =
            [
                new Vector3(10f, -1f, 0f),
                new Vector3(10f, 1f, 0f),
                new Vector3(10f, 1f, 2f),
            ],
            Plane = new Plane(Vector3.UnitX, -10f),
            NumPoints = 3,
            SidesType = CullMode.None,
        };
        var startCell = new CellPhysics
        {
            BSP = new PhysicsBSPTree
            {
                Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf },
            },
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode { Type = BSPNodeType.Leaf },
            },
            Portals =
            [
                new PortalInfo(
                    (ushort)(rejected & 0xFFFFu),
                    portalPolygonId,
                    0),
            ],
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon>
            {
                [portalPolygonId] = portalPolygon,
            },
        };
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(start, startCell);
        cache.RegisterCellStructForTest(
            rejected,
            ContainmentCell(
                new Plane(Vector3.UnitX, -100f),
                []));
        var candidates = new CellArray();
        var queryFootprint = new CellArray();
        candidates.UnionTarget = queryFootprint;
        var spheres = new[]
        {
            new Sphere
            {
                Origin = Vector3.Zero,
                Radius = 0.48f,
            },
        };

        uint containing = CellTransit.FindCellSet(
            cache,
            spheres,
            spheres.Length,
            start,
            candidates);

        Assert.Equal(start, containing);
        Assert.Equal(new[] { start }, candidates.OrderedIds);
        Assert.Equal(new[] { start, rejected }, queryFootprint.OrderedIds);
    }

    [Fact]
    public void RejectedBuildingContainmentProbeIsRecordedUnionOnly()
    {
        const uint rejected = Landblock | 0x0102u;
        var building = new BuildingPhysics
        {
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Portals =
            [
                new BldPortalInfo(
                    rejected,
                    otherPortalId: 0,
                    flags: 0),
            ],
        };
        var candidates = new CellArray();
        var queryFootprint = new CellArray();
        candidates.UnionTarget = queryFootprint;

        CellTransit.CheckBuildingTransit(
            new PhysicsDataCache(),
            building,
            Vector3.Zero,
            sphereRadius: 0.48f,
            candidates);

        Assert.Empty(candidates);
        Assert.Equal(new[] { rejected }, queryFootprint.OrderedIds);
    }

    [Fact]
    public void OutdoorBlockSentinel_IsAnIndoorShapedLostCellSentinel()
    {
        var engine = new PhysicsEngine();
        const uint sentinel = Landblock | 0xFFFFu;
        var local = new Vector3(25f, 49f, 7f);
        PhysicsSetPositionResult result = engine.SetPosition(
            Request(sentinel, local, local));

        Assert.True(result.IsDeferred);
        Assert.Equal(sentinel, result.CellId);
        Assert.Equal(local, result.CellLocalPosition);
    }

    [Fact]
    public void InvalidLowCellId_ParksWithoutRewritingAuthoritativeFrame()
    {
        var engine = new PhysicsEngine();
        const uint invalid = Landblock | 0x0050u;
        var local = new Vector3(11f, 13f, 17f);

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(invalid, local, local));

        Assert.True(result.IsDeferred);
        Assert.Equal(PhysicsSetPositionError.Ok, result.Error);
        Assert.Equal(invalid, result.CellId);
        Assert.Equal(local, result.CellLocalPosition);
    }

    [Fact]
    public void MissingIndoorCell_ParksExactCellAndFrame()
    {
        var engine = new PhysicsEngine();
        const uint indoor = Landblock | 0x0107u;
        var local = new Vector3(31f, -4f, 9f);

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(indoor, local, new Vector3(31f, -4f, 9f)));

        Assert.True(result.IsDeferred);
        Assert.Equal(indoor, result.CellId);
        Assert.Equal(local, result.CellLocalPosition);
    }

    [Fact]
    public void EmptySphereList_UsesRetailDummyAtScaleOne()
    {
        PhysicsEngine engine = FlatEngine();
        Vector3 capturedOrigin = default;
        float capturedRadius = 0f;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    capturedOrigin = transition.SpherePath.LocalSphere[0].Origin;
                    capturedRadius = transition.SpherePath.LocalSphere[0].Radius;
                }
                return observed;
            };

        _ = engine.SetPosition(Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f),
            scale: -7f));

        Assert.Equal(new Vector3(0f, 0f, 0.1f), capturedOrigin);
        Assert.Equal(0.1f, capturedRadius);
    }

    [Fact]
    public void AuthoredSphereList_CapsAtTwoAndPreservesNonPositiveScale()
    {
        PhysicsEngine engine = FlatEngine();
        int count = 0;
        Vector3 firstOrigin = default;
        float firstRadius = 0f;
        Vector3 secondOrigin = default;
        float secondRadius = 0f;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    count = transition.SpherePath.NumSphere;
                    firstOrigin = transition.SpherePath.LocalSphere[0].Origin;
                    firstRadius = transition.SpherePath.LocalSphere[0].Radius;
                    secondOrigin = transition.SpherePath.LocalSphere[1].Origin;
                    secondRadius = transition.SpherePath.LocalSphere[1].Radius;
                }
                return TransitionState.Collided;
            };
        ImmutableArray<FlatCollisionSphere> spheres = ImmutableArray.Create(
            new FlatCollisionSphere(new Vector3(1f, 2f, 3f), 4f),
            new FlatCollisionSphere(new Vector3(5f, 6f, 7f), 8f),
            new FlatCollisionSphere(new Vector3(9f), 10f));

        _ = engine.SetPosition(Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f),
            spheres,
            scale: -2f));

        Assert.Equal(2, count);
        Assert.Equal(new Vector3(-2f, -4f, -6f), firstOrigin);
        Assert.Equal(-8f, firstRadius);
        Assert.Equal(new Vector3(-10f, -12f, -14f), secondOrigin);
        Assert.Equal(-16f, secondRadius);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void StepDown_IsDisabledOnlyForMissiles(
        bool missile,
        bool expectedStepDown)
    {
        PhysicsEngine engine = FlatEngine();
        bool? captured = null;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                    captured = transition.ObjectInfo.StepDown;
                return observed;
            };
        PhysicsSetPositionRequest request = Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f)) with
        {
            MoverPhysicsState = missile
                ? PhysicsStateFlags.Missile
                : PhysicsStateFlags.Gravity,
        };

        _ = engine.SetPosition(request);

        Assert.Equal(expectedStepDown, captured);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ObjectInfoEthereal_IsSeededFromPhysicsState(
        bool ethereal,
        bool expected)
    {
        PhysicsEngine engine = FlatEngine();
        bool? captured = null;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                    captured = transition.ObjectInfo.Ethereal;
                return observed;
            };

        _ = engine.SetPosition(Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                MoverPhysicsState = ethereal
                    ? PhysicsStateFlags.Ethereal
                    : PhysicsStateFlags.Gravity,
            });

        Assert.Equal(expected, captured);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void PathClipped_ComesOnlyFromExplicitObjectInfoFlags(
        bool explicitPathClipped,
        bool expected)
    {
        PhysicsEngine engine = FlatEngine();
        bool? captured = null;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                    captured = transition.ObjectInfo.PathClipped;
                return observed;
            };

        _ = engine.SetPosition(Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
                MoverFlags = explicitPathClipped
                    ? ObjectInfoState.PathClipped
                    : ObjectInfoState.None,
            });

        Assert.Equal(expected, captured);
    }

    [Fact]
    public void DoNotCreateCells_MissingDestinationStillDefersWithoutCreation()
    {
        var engine = new PhysicsEngine();

        PhysicsSetPositionResult result = engine.SetPosition(Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                Flags = PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Slide
                    | PhysicsSetPositionFlags.DoNotCreateCells,
            });

        Assert.True(result.IsDeferred);
        Assert.Equal(0, engine.LandblockCount);
    }

    [Fact]
    public void SuccessfulPlacement_RetainsRequestedOrientation()
    {
        PhysicsEngine engine = FlatEngine();
        PhysicsSetPositionRequest request = Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f));

        PhysicsSetPositionResult result = engine.SetPosition(request);

        Assert.True(result.IsCommitted);
        Assert.Equal(request.Orientation, result.Orientation);
    }

    [Fact]
    public void OuterPlacementStartsWithInitialPlacementInsert()
    {
        PhysicsEngine engine = FlatEngine();
        InsertType? captured = null;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment
                    && captured is null)
                {
                    captured = transition.SpherePath.InsertType;
                }
                return observed;
            };

        _ = engine.SetPosition(Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f)));

        Assert.Equal(InsertType.InitialPlacement, captured);
    }

    [Theory]
    [InlineData(1, 0.5f, 1.0f, 0.25f, 1)]
    [InlineData(2, 0.5f, 1.0f, 0.5f, 2)]
    [InlineData(1, 0.5f, 0.4f, 0.4f, 1)]
    public void StepDownProbePlan_PreservesRetailEqualityBoundaries(
        int sphereCount,
        float radius,
        float requested,
        float expectedHeight,
        int expectedCount)
    {
        Assert.Equal(
            (expectedHeight, expectedCount),
            Transition.GetStepDownProbePlan(
                sphereCount,
                radius,
                requested));
    }

    [Theory]
    [InlineData(-100f, 0f, 7f, 7u, true)]
    [InlineData(0.0500000007f, 0.0500000007f, 500f, 7u, true)]
    [InlineData(0.0501f, 0f, 0f, 7u, false)]
    [InlineData(0f, 0.0501f, 0f, 7u, false)]
    [InlineData(0f, 0f, 0f, 8u, false)]
    public void NoSlideAcceptance_IsSignedXYSameCellAndIgnoresZ(
        float dx,
        float dy,
        float dz,
        uint resolvedCell,
        bool expected)
    {
        Assert.Equal(
            expected,
            PhysicsEngine.AcceptNoSlidePlacement(
                new Vector3(dx, dy, dz),
                Vector3.Zero,
                resolvedCell,
                adjustedCellId: 7u));
    }

    [Fact]
    public void PlacementCompass_LateSampleMatchesRetailFloatBits()
    {
        PhysicsEngine engine = FlatEngine();
        var transition = new Transition();
        transition.SpherePath.InitPath(
            Vector3.Zero,
            Vector3.Zero,
            Cell,
            sphereRadius: 0.48f);
        transition.SpherePath.InsertType = InsertType.Placement;
        transition.SpherePath.PlacementAllowsSliding = true;
        Vector3 lastCheck = default;
        engine.TransitionCellCollisionTestHook =
            (observed, phase, _, _) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                    lastCheck = observed.SpherePath.CheckPos;
                return TransitionState.Collided;
            };

        Assert.False(transition.FindPlacementPos(engine));

        Assert.Equal(unchecked((int)0xBEEDC24F),
            BitConverter.SingleToInt32Bits(lastCheck.X));
        Assert.Equal(unchecked((int)0x407E44DE),
            BitConverter.SingleToInt32Bits(lastCheck.Y));
    }

    [Theory]
    [InlineData(TransitionState.Collided)]
    [InlineData(TransitionState.Adjusted)]
    [InlineData(TransitionState.Slid)]
    public void InnerPlacementValidator_ResetsCollisionInfoForEveryFailure(
        TransitionState state)
    {
        Transition transition = PlacementTransition();
        transition.SpherePath.PlacementAllowsSliding = true;
        transition.CollisionInfo.SetContactPlane(
            new Plane(Vector3.UnitZ, 0f),
            Cell);
        transition.CollisionInfo.SetSlidingNormal(Vector3.UnitX);

        TransitionState result =
            transition.ValidatePlacementTransitionForTest(state);

        Assert.Equal(state, result);
        Assert.False(transition.CollisionInfo.ContactPlaneValid);
        Assert.False(transition.CollisionInfo.SlidingNormalValid);
    }

    [Fact]
    public void InnerPlacementValidator_DoesNotResetWhenSlidingDisabled()
    {
        Transition transition = PlacementTransition();
        transition.SpherePath.PlacementAllowsSliding = false;
        transition.CollisionInfo.SetContactPlane(
            new Plane(Vector3.UnitZ, 0f),
            Cell);

        _ = transition.ValidatePlacementTransitionForTest(
            TransitionState.Collided);

        Assert.True(transition.CollisionInfo.ContactPlaneValid);
    }

    [Fact]
    public void OuterPlacementValidator_CollidedNeverRetriesOrResets()
    {
        PhysicsEngine engine = FlatEngine();
        int passes = 0;
        engine.TransitionCellCollisionTestHook =
            (_, _, _, observed) =>
            {
                passes++;
                return observed;
            };
        Transition transition = PlacementTransition();
        transition.CollisionInfo.SetContactPlane(
            new Plane(Vector3.UnitZ, 0f),
            Cell);

        TransitionState result = transition.ValidatePlacementForTest(
            engine,
            TransitionState.Collided,
            retryPlacement: true);

        Assert.Equal(TransitionState.Collided, result);
        Assert.Equal(0, passes);
        Assert.True(transition.CollisionInfo.ContactPlaneValid);
    }

    [Theory]
    [InlineData(TransitionState.Adjusted)]
    [InlineData(TransitionState.Slid)]
    public void OuterPlacementValidator_AdjustedAndSlidRetryExactlyOnce(
        TransitionState state)
    {
        PhysicsEngine engine = FlatEngine();
        int environmentPasses = 0;
        engine.TransitionCellCollisionTestHook =
            (_, phase, _, _) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                    environmentPasses++;
                return TransitionState.OK;
            };
        Transition transition = PlacementTransition();

        TransitionState result = transition.ValidatePlacementForTest(
            engine,
            state,
            retryPlacement: true);

        Assert.Equal(TransitionState.OK, result);
        Assert.Equal(1, environmentPasses);
    }

    [Theory]
    [InlineData(TransitionState.Adjusted)]
    [InlineData(TransitionState.Slid)]
    public void OuterPlacementValidator_RetryFalseReturnsOriginalState(
        TransitionState state)
    {
        PhysicsEngine engine = FlatEngine();
        int passes = 0;
        engine.TransitionCellCollisionTestHook =
            (_, _, _, observed) =>
            {
                passes++;
                return observed;
            };
        Transition transition = PlacementTransition();

        TransitionState result = transition.ValidatePlacementForTest(
            engine,
            state,
            retryPlacement: false);

        Assert.Equal(state, result);
        Assert.Equal(0, passes);
    }

    [Theory]
    [InlineData(false, (int)PhysicsSetPositionError.NoValidPosition)]
    [InlineData(true, (int)PhysicsSetPositionError.Collided)]
    public void FailedCheck_MapsCollisionHandlerResultToRetailError(
        bool handled,
        int expectedValue)
    {
        var expected = (PhysicsSetPositionError)expectedValue;
        PhysicsEngine engine = FlatEngine();
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    transition.CollisionInfo.CollidedWithEnvironment = true;
                    transition.CollisionInfo.InitContactPlane(
                        new Plane(Vector3.UnitZ, -3f),
                        Cell,
                        isWater: true);
                    transition.CollisionInfo.SetSlidingNormal(Vector3.UnitX);
                    transition.CollisionInfo.SetCollisionNormal(-Vector3.UnitY);
                    transition.CollisionInfo.FramesStationaryFall = 2;
                    transition.CollisionInfo.AdjustOffset = new Vector3(1f, 2f, 3f);
                    transition.CollisionInfo.CollideObjectGuids.Add(0x12345678u);
                    transition.CollisionInfo.LastCollidedObjectGuid = 0x12345678u;
                }
                return TransitionState.Collided;
            };
        PhysicsSetPositionCollisionReport observedReport = default;

        PhysicsSetPositionResult result = engine.SetPosition(Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f)), report =>
            {
                observedReport = report;
                return handled;
            });

        Assert.Equal(expected, result.Error);
        Assert.Equal(handled, result.CollisionHandlerResult);
        Assert.True(observedReport.CollidedWithEnvironment);
        Assert.True(observedReport.ContactPlaneValid);
        Assert.Equal(new Plane(Vector3.UnitZ, -3f), observedReport.ContactPlane);
        Assert.Equal(Cell, observedReport.ContactPlaneCellId);
        Assert.True(observedReport.ContactPlaneIsWater);
        Assert.True(observedReport.LastKnownContactPlaneValid);
        Assert.Equal(
            observedReport.ContactPlane,
            observedReport.LastKnownContactPlane);
        Assert.Equal(Cell, observedReport.LastKnownContactPlaneCellId);
        Assert.True(observedReport.LastKnownContactPlaneIsWater);
        Assert.True(observedReport.SlidingNormalValid);
        Assert.Equal(Vector3.UnitX, observedReport.SlidingNormal);
        Assert.True(observedReport.CollisionNormalValid);
        Assert.Equal(-Vector3.UnitY, observedReport.CollisionNormal);
        Assert.Equal(2, observedReport.FramesStationaryFall);
        Assert.Equal(new Vector3(1f, 2f, 3f), observedReport.AdjustOffset);
        Assert.Equal(0x12345678u, observedReport.LastCollidedObjectId);
        Assert.Equal(
            new[] { 0x12345678u },
            observedReport.CollidedObjectIds.ToArray());
        Assert.True(result.CollidedWithEnvironment);
        Assert.True(result.InContact);
        Assert.True(result.OnWalkable);
        Assert.True(result.ContactPlaneIsWater);
        Assert.True(result.SlidingNormalValid);
        Assert.True(result.CollisionNormalValid);
        Assert.Equal(2, result.FramesStationaryFall);
        Assert.Equal(
            new[] { 0x12345678u },
            result.CollidedObjectIds.ToArray());
        Assert.Equal(PhysicsResidenceDisposition.Unchanged, result.Residence);
    }

    [Theory]
    [InlineData((int)PhysicsPlacementClass.Hook)]
    [InlineData((int)PhysicsPlacementClass.Storage)]
    [InlineData((int)PhysicsPlacementClass.Corpse)]
    public void RetailForceClasses_BypassPlacementCollision(
        int placementClassValue)
    {
        var placementClass = (PhysicsPlacementClass)placementClassValue;
        PhysicsEngine engine = FlatEngine();
        int collisionPasses = 0;
        engine.TransitionCellCollisionTestHook =
            (_, _, _, observed) =>
            {
                collisionPasses++;
                return observed;
            };

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                PlacementClass = placementClass,
            });

        Assert.True(result.IsCommitted);
        Assert.Equal(0, collisionPasses);
        Assert.True(result.CellChanged);
        Assert.Equal(
            PhysicsShadowCommitAction.Recalculate,
            result.ShadowAction);
        Assert.Empty(result.CrossCellIds);
    }

    [Fact]
    public void OrdinaryObject_CannotRequestForceByFlags()
    {
        PhysicsEngine engine = FlatEngine();
        int collisionPasses = 0;
        engine.TransitionCellCollisionTestHook =
            (_, _, _, observed) =>
            {
                collisionPasses++;
                return observed;
            };

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                PlacementClass = PhysicsPlacementClass.Ordinary,
                Flags = (PhysicsSetPositionFlags)uint.MaxValue
                    & ~PhysicsSetPositionFlags.RandomScatter
                    & ~PhysicsSetPositionFlags.Scatter,
            });

        Assert.True(result.IsCommitted);
        Assert.True(collisionPasses > 0);
    }

    [Fact]
    public void ForceIntoSameCell_SetsFrameWithoutRecalculatingCrossCells()
    {
        PhysicsEngine engine = FlatEngine();
        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                PlacementClass = PhysicsPlacementClass.Corpse,
                CurrentCellId = Cell,
            });

        Assert.True(result.IsCommitted);
        Assert.False(result.CellChanged);
        Assert.Equal(PhysicsShadowCommitAction.None, result.ShadowAction);
        Assert.Empty(result.CrossCellIds);
    }

    [Fact]
    public void ForceIntoChangedCell_RequestsCanonicalShadowRecalculation()
    {
        PhysicsEngine engine = FlatEngine();
        var position = new Vector3(23.8f, 12f, 2.5f);
        ImmutableArray<FlatCollisionSphere> spheres = ImmutableArray.Create(
            new FlatCollisionSphere(Vector3.Zero, 0.5f));

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(Cell, position, position, spheres) with
            {
                PlacementClass = PhysicsPlacementClass.Hook,
                CurrentCellId = Landblock | 0x0040u,
            });

        Assert.True(result.IsCommitted);
        Assert.True(result.CellChanged);
        Assert.Equal(
            PhysicsShadowCommitAction.Recalculate,
            result.ShadowAction);
        Assert.Empty(result.CrossCellIds);
    }

    [Fact]
    public void ForceIntoRetainedCellWithNullCurrentPointer_RefloodsShadows()
    {
        PhysicsEngine engine = FlatEngine();

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                PlacementClass = PhysicsPlacementClass.Corpse,
                CurrentCellId = null,
            });

        Assert.True(result.IsCommitted);
        Assert.True(result.CellChanged);
        Assert.Equal(
            PhysicsShadowCommitAction.Recalculate,
            result.ShadowAction);
    }

    [Fact]
    public void OrdinaryPhysicsBspPlacement_RequestsCanonicalShadowRecalculation()
    {
        PhysicsEngine engine = FlatEngine();

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.HasPhysicsBsp
                    | PhysicsStateFlags.Missile,
            });

        Assert.True(result.IsCommitted);
        Assert.Equal(
            PhysicsShadowCommitAction.Recalculate,
            result.ShadowAction);
        Assert.Empty(result.CrossCellIds);
    }

    [Fact]
    public void OrdinarySpherePlacement_ReplacesShadowsWithTransitionCells()
    {
        PhysicsEngine engine = FlatEngine();

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
            });

        Assert.True(result.IsCommitted);
        Assert.Equal(PhysicsShadowCommitAction.Replace, result.ShadowAction);
        Assert.Contains(Cell, result.CrossCellIds);
    }

    [Fact]
    public void OrdinarySpherePlacementWithNoTransitionCells_PreservesShadows()
    {
        PhysicsEngine engine = FlatEngine(withDataCache: false);

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
            });

        Assert.True(result.IsCommitted);
        Assert.Equal(PhysicsShadowCommitAction.Preserve, result.ShadowAction);
        Assert.Empty(result.CrossCellIds);
    }

    [Fact]
    public void RandomScatter_IsDirectAndStopsOnDeferredOk()
    {
        var engine = new PhysicsEngine();
        var random = new Queue<double>(new[] { 1d, 0d, 0.25d, 0.75d });
        int draws = 0;
        engine.SetPositionRandomUnit = () =>
        {
            draws++;
            return random.Dequeue();
        };
        PhysicsSetPositionRequest request = Request(
            Cell,
            new Vector3(10f, 10f, 3f),
            new Vector3(10f, 10f, 3f)) with
        {
            Flags = PhysicsSetPositionFlags.RandomScatter,
            ScatterRadiusX = 4f,
            ScatterRadiusY = 2f,
            ScatterAttempts = 2u,
        };

        PhysicsSetPositionResult result = engine.SetPosition(request);

        Assert.True(result.IsDeferred);
        Assert.Equal(2, draws);
        Assert.Equal(new Vector3(14f, 8f, 3f), result.Position);
    }

    [Fact]
    public void ScatterFallback_RunsOnlyAfterNormalError_AndUsesExactAttempts()
    {
        PhysicsEngine engine = FlatEngine();
        int collisionPasses = 0;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, _) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment)
                {
                    collisionPasses++;
                    transition.CollisionInfo.CollidedWithEnvironment = true;
                }
                return TransitionState.Collided;
            };
        var random = new Queue<double>(new[] { 0d, 0d, 1d, 1d });
        int draws = 0;
        engine.SetPositionRandomUnit = () =>
        {
            draws++;
            return random.Dequeue();
        };
        PhysicsSetPositionRequest request = Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f)) with
        {
            Flags = PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Scatter,
            ScatterRadiusX = 1f,
            ScatterRadiusY = 1f,
            ScatterAttempts = 2u,
        };

        PhysicsSetPositionResult result = engine.SetPosition(request);

        Assert.Equal(PhysicsSetPositionError.NoValidPosition, result.Error);
        Assert.Equal(3, collisionPasses);
        Assert.Equal(4, draws);
        Assert.Equal(new Vector3(11f, 11f, 10f), result.Position);
    }

    [Fact]
    public void NormalThenScatterUnionsEveryLandblockProbeInStableOrder()
    {
        PhysicsEngine engine = FlatEngine();
        const uint eastLandblock = 0xAAB40000u;
        AddFlatLandblock(engine, eastLandblock, worldOffsetX: 192f);
        int pass = 0;
        engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) => phase
                is TransitionCellCollisionPhase.Environment
                && pass++ == 0
                    ? TransitionState.Collided
                    : observed;
        var random = new Queue<double>([1d, 0.5d]);
        engine.SetPositionRandomUnit = random.Dequeue;

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(180f, 12f, 7f),
                new Vector3(180f, 12f, 7f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
                Flags = PhysicsSetPositionFlags.Placement
                    | PhysicsSetPositionFlags.Scatter,
                ScatterRadiusX = 20f,
                ScatterAttempts = 1u,
            });

        Assert.True(result.IsCommitted);
        int source = IndexOfLandblock(result.QueriedCellIds, Landblock);
        int east = IndexOfLandblock(
            result.QueriedCellIds,
            eastLandblock);
        Assert.True(source >= 0);
        Assert.True(east > source);
    }

    [Fact]
    public void MultiScatterUnionsFailedAndSuccessfulAttemptLandblocks()
    {
        PhysicsEngine engine = FlatEngine();
        const uint westLandblock = 0xA8B40000u;
        const uint eastLandblock = 0xAAB40000u;
        AddFlatLandblock(engine, westLandblock, worldOffsetX: -192f);
        AddFlatLandblock(engine, eastLandblock, worldOffsetX: 192f);
        int pass = 0;
        engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) => phase
                is TransitionCellCollisionPhase.Environment
                && pass++ == 0
                    ? TransitionState.Collided
                    : observed;
        var random = new Queue<double>([0d, 0.5d, 1d, 0.5d]);
        engine.SetPositionRandomUnit = random.Dequeue;

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 12f, 7f),
                new Vector3(10f, 12f, 7f)) with
            {
                MoverPhysicsState = PhysicsStateFlags.Missile,
                Flags = PhysicsSetPositionFlags.RandomScatter,
                ScatterRadiusX = 200f,
                ScatterAttempts = 2u,
            });

        Assert.True(result.IsCommitted);
        int west = IndexOfLandblock(
            result.QueriedCellIds,
            westLandblock);
        int east = IndexOfLandblock(
            result.QueriedCellIds,
            eastLandblock);
        Assert.True(west >= 0);
        Assert.True(east > west);
        Assert.DoesNotContain(
            result.CrossCellIds,
            cell => (cell & 0xFFFF0000u) == westLandblock);
        Assert.Contains(
            result.CrossCellIds,
            cell => (cell & 0xFFFF0000u) == eastLandblock);
    }

    [Fact]
    public void Scatter_ReusesOneTransitionAndFailedInnerProbeCannotLeakIntoSuccess()
    {
        PhysicsEngine engine = FlatEngine();
        int placementPasses = 0;
        bool injectedFailure = false;
        engine.TransitionCellCollisionTestHook =
            (transition, phase, _, observed) =>
            {
                if (phase != TransitionCellCollisionPhase.Environment)
                    return observed;

                if (transition.SpherePath.InsertType == InsertType.Placement)
                {
                    placementPasses++;
                    if (!injectedFailure)
                    {
                        injectedFailure = true;
                        transition.CollisionInfo.CollidedWithEnvironment = true;
                        transition.CollisionInfo.SetContactPlane(
                            new Plane(Vector3.UnitZ, 0f),
                            Cell);
                        transition.CollisionInfo.SetSlidingNormal(Vector3.UnitX);
                        return TransitionState.Collided;
                    }
                }
                return TransitionState.OK;
            };
        engine.SetPositionRandomUnit = () => 0.5d;

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(
                Cell,
                new Vector3(10f, 10f, 10f),
                new Vector3(10f, 10f, 10f)) with
            {
                Flags = PhysicsSetPositionFlags.RandomScatter
                    | PhysicsSetPositionFlags.Slide,
                ScatterAttempts = 2u,
                MoverPhysicsState = PhysicsStateFlags.Missile,
            });

        Assert.True(result.IsCommitted);
        Assert.True(injectedFailure);
        Assert.True(placementPasses >= 2);
        Assert.False(result.InContact);
        Assert.False(result.SlidingNormalValid);
        Assert.False(result.CollidedWithEnvironment);
        Assert.Empty(result.CollidedObjectIds);
    }

    [Fact]
    public void EleventhNestedSetPosition_ReturnsGeneralFailureWithoutThrowing()
    {
        PhysicsEngine engine = FlatEngine();
        PhysicsSetPositionRequest request = Request(
            Cell,
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f));
        PhysicsSetPositionResult capacityResult = default;
        int callbackDepth = 0;
        engine.TransitionCellCollisionTestHook =
            (_, phase, _, observed) =>
            {
                if (phase != TransitionCellCollisionPhase.Environment)
                    return observed;

                callbackDepth++;
                PhysicsSetPositionResult nested = engine.SetPosition(request);
                if (nested.Error == PhysicsSetPositionError.GeneralFailure)
                    capacityResult = nested;
                return TransitionState.Collided;
            };

        PhysicsSetPositionResult outer = engine.SetPosition(request);

        Assert.Equal(PhysicsSetPositionError.NoValidPosition, outer.Error);
        Assert.Equal(10, callbackDepth);
        Assert.Equal(
            PhysicsSetPositionError.GeneralFailure,
            capacityResult.Error);
        Assert.Equal(
            PhysicsResidenceDisposition.Unchanged,
            capacityResult.Residence);
    }

    [Fact]
    public void ZeroScatterAttempts_ReturnsGeneralFailureWithoutRandomDraw()
    {
        var engine = new PhysicsEngine();
        int draws = 0;
        engine.SetPositionRandomUnit = () =>
        {
            draws++;
            return 0.5d;
        };

        PhysicsSetPositionResult result = engine.SetPosition(
            Request(Cell, Vector3.One, Vector3.One) with
            {
                Flags = PhysicsSetPositionFlags.RandomScatter,
                ScatterAttempts = 0u,
            });

        Assert.Equal(
            PhysicsSetPositionError.GeneralFailure,
            result.Error);
        Assert.Equal(PhysicsResidenceDisposition.Unchanged, result.Residence);
        Assert.Equal(0, draws);
    }

    private static PhysicsSetPositionRequest Request(
        uint cellId,
        Vector3 cellLocal,
        Vector3 position,
        ImmutableArray<FlatCollisionSphere> spheres = default,
        float scale = 1f) => new(
            position,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.75f),
            cellId,
            cellLocal,
            spheres,
            scale,
            StepUpHeight: 0.4f,
            StepDownHeight: 0.4f,
            Flags: PhysicsSetPositionFlags.Placement
                | PhysicsSetPositionFlags.Slide);

    private static Transition PlacementTransition()
    {
        var transition = new Transition();
        transition.SpherePath.InitPath(
            new Vector3(10f, 10f, 10f),
            new Vector3(10f, 10f, 10f),
            Cell,
            sphereRadius: 0.48f);
        transition.SpherePath.InsertType = InsertType.Placement;
        return transition;
    }

    private static PhysicsEngine FlatEngine(bool withDataCache = true)
    {
        var engine = new PhysicsEngine();
        if (withDataCache)
            engine.DataCache = new PhysicsDataCache();
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }

    private static void AddFlatLandblock(
        PhysicsEngine engine,
        uint landblock,
        float worldOffsetX = 0f,
        float worldOffsetY = 0f)
    {
        engine.AddLandblock(
            landblock,
            new TerrainSurface(new byte[81], new float[256]),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX,
            worldOffsetY);
    }

    private static CellPhysics ContainmentCell(
        Plane plane,
        uint[] visibleCells) => new()
        {
            BSP = new PhysicsBSPTree
            {
                Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf },
            },
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            CellBSP = new CellBSPTree
            {
                Root = new CellBSPNode
                {
                    SplittingPlane = plane,
                    PosNode = new CellBSPNode { Type = BSPNodeType.Leaf },
                },
            },
            Portals = [new PortalInfo(0xFFFF, 0, 0)],
            PortalPolygons = new Dictionary<ushort, ResolvedPolygon>(),
            VisibleCellIds = new HashSet<uint>(visibleCells),
        };

    private static int IndexOfLandblock(
        ImmutableArray<uint> cells,
        uint landblock)
    {
        for (int index = 0; index < cells.Length; index++)
        {
            if ((cells[index] & 0xFFFF0000u)
                == (landblock & 0xFFFF0000u))
            {
                return index;
            }
        }
        return -1;
    }
}
