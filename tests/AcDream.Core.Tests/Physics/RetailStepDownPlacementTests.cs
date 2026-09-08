using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class RetailStepDownPlacementTests
{
    private const uint Cell = 0xA9B40001u;
    private const float Radius = 0.48f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryContactMaintenance_AlwaysRunsFinalPlacement(bool twoSpheres)
    {
        Transition transition = MakeGroundedTransition(twoSpheres);
        const float supportWalkInterp = 0.375f;
        int supportPasses = 0;
        int placementPasses = 0;
        uint placementWalkInterpBits = 0;
        var engine = new PhysicsEngine
        {
            TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
            {
                if (phase != TransitionCellCollisionPhase.Environment)
                    return actual;

                if (candidate.SpherePath.StepDown)
                {
                    supportPasses++;
                    candidate.SpherePath.WalkInterp = supportWalkInterp;
                    candidate.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f), Cell);
                }
                else if (candidate.SpherePath.InsertType == InsertType.Placement)
                {
                    placementPasses++;
                    placementWalkInterpBits = BitConverter.SingleToUInt32Bits(
                        candidate.SpherePath.WalkInterp);
                }

                return actual;
            },
        };

        TransitionState result = transition.TransitionalInsertForTest(1, engine);

        Assert.Equal(TransitionState.OK, result);
        Assert.Equal(1, supportPasses);
        Assert.Equal(1, placementPasses);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(supportWalkInterp),
            placementWalkInterpBits);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(supportWalkInterp),
            BitConverter.SingleToUInt32Bits(transition.SpherePath.WalkInterp));
        Assert.Equal(InsertType.Transition, transition.SpherePath.InsertType);
        Assert.False(transition.SpherePath.StepDown);
    }

    [Fact]
    public void CheckWalkable_FailedNestedProbe_PreservesOuterBackupForEdgeSlide()
    {
        Transition transition = MakeGroundedTransition(twoSpheres: true);
        var sp = transition.SpherePath;
        Vector3 outerBackup = new(91.125f, -17.25f, 333.5f);
        const uint outerBackupCell = 0xA9B40077u;
        Vector3 nestedOrigin = new(2.125f, 3.25f, 4.5f);

        sp.SetCheckPos(nestedOrigin, Cell);
        sp.SetWalkable(
            new Plane(Vector3.UnitZ, 0f),
            [
                new(100f, 100f, 0f),
                new(101f, 100f, 0f),
                new(101f, 101f, 0f),
                new(100f, 101f, 0f),
            ],
            Vector3.UnitZ);
        sp.BackupCheckPos = outerBackup;
        sp.BackupCheckCellId = outerBackupCell;

        int nestedProbes = 0;
        var engine = new PhysicsEngine
        {
            TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
            {
                if (phase == TransitionCellCollisionPhase.Environment
                    && candidate.SpherePath.CheckWalkable)
                {
                    nestedProbes++;
                }

                return actual;
            },
        };

        bool walkable = transition.DoCheckWalkable(PhysicsGlobals.FloorZ, engine);

        Assert.False(walkable);
        Assert.True(nestedProbes > 0);
        AssertVectorBits(nestedOrigin, sp.CheckPos);
        Assert.Equal(Cell, sp.CheckCellId);
        AssertVectorBits(outerBackup, sp.BackupCheckPos);
        Assert.Equal(outerBackupCell, sp.BackupCheckCellId);

        transition.ObjectInfo.State &= ~ObjectInfoState.EdgeSlide;
        sp.SetCheckPos(new Vector3(-8f, -9f, -10f), Cell);
        bool stop = transition.EdgeSlideAfterStepDownFailedForTest(
            engine,
            stepDownHeight: 0.04f,
            zVal: PhysicsGlobals.FloorZ,
            out TransitionState state);

        Assert.True(stop);
        Assert.Equal(TransitionState.OK, state);
        AssertVectorBits(outerBackup, sp.CheckPos);
        Assert.Equal(outerBackupCell, sp.CheckCellId);
    }

    [Fact]
    public void SupportedCandidate_OverlappingDuringPlacement_IsRejected()
    {
        Transition transition = MakeGroundedTransition(twoSpheres: true);
        int placementPasses = 0;
        var engine = new PhysicsEngine
        {
            TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
            {
                if (phase != TransitionCellCollisionPhase.Environment)
                    return actual;

                if (candidate.SpherePath.StepDown)
                {
                    candidate.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f), Cell);
                    return actual;
                }

                if (candidate.SpherePath.InsertType == InsertType.Placement)
                {
                    placementPasses++;
                    return TransitionState.Collided;
                }

                return actual;
            },
        };

        bool accepted = transition.DoStepDownForTest(
            stepDownHeight: 0.04f,
            walkableZ: PhysicsGlobals.FloorZ,
            engine);

        Assert.False(accepted);
        Assert.Equal(1, placementPasses);
        Assert.Equal(InsertType.Transition, transition.SpherePath.InsertType);
        Assert.False(transition.SpherePath.StepDown);
    }

    [Fact]
    public void StepUp_UsesTheSameFinalPlacementPass()
    {
        Transition transition = MakeGroundedTransition(twoSpheres: true);
        int placementPasses = 0;
        var engine = new PhysicsEngine
        {
            TransitionCellCollisionTestHook = (candidate, phase, _, actual) =>
            {
                if (phase != TransitionCellCollisionPhase.Environment)
                    return actual;

                if (candidate.SpherePath.StepDown)
                {
                    candidate.CollisionInfo.SetContactPlane(
                        new Plane(Vector3.UnitZ, 0f), Cell);
                }
                else if (candidate.SpherePath.InsertType == InsertType.Placement)
                {
                    placementPasses++;
                }

                return actual;
            },
        };

        bool accepted = transition.DoStepUp(Vector3.UnitX, engine);

        Assert.True(accepted);
        Assert.Equal(1, placementPasses);
        Assert.Equal(InsertType.Transition, transition.SpherePath.InsertType);
        Assert.False(transition.SpherePath.StepUp);
        Assert.False(transition.SpherePath.StepDown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlacementDispatcher_AllowsExactWallTangency_AndRejectsOverlap(
        bool twoSpheres)
    {
        (PhysicsBSPNode root, Dictionary<ushort, ResolvedPolygon> resolved) =
            BuildWall();
        FlatPhysicsBsp flat =
            FlatCollisionAssetBuilder.FlattenPhysicsBsp(root, resolved);

        float tangentY = Radius;
        TransitionState graphTangent = RunPlacement(
            root, resolved, flat: null, tangentY, twoSpheres);
        TransitionState flatTangent = RunPlacement(
            root: null, resolved, flat, tangentY, twoSpheres);
        TransitionState graphOverlap = RunPlacement(
            root, resolved, flat: null,
            Radius - PhysicsGlobals.EPSILON * 2f,
            twoSpheres);
        TransitionState flatOverlap = RunPlacement(
            root: null, resolved, flat,
            Radius - PhysicsGlobals.EPSILON * 2f,
            twoSpheres);

        Assert.Equal(TransitionState.OK, graphTangent);
        Assert.Equal(graphTangent, flatTangent);
        Assert.Equal(TransitionState.Collided, graphOverlap);
        Assert.Equal(graphOverlap, flatOverlap);
    }

    private static TransitionState RunPlacement(
        PhysicsBSPNode? root,
        Dictionary<ushort, ResolvedPolygon> resolved,
        FlatPhysicsBsp? flat,
        float centerY,
        bool twoSpheres)
    {
        var foot = new Sphere
        {
            Origin = new Vector3(0f, centerY, 0f),
            Radius = Radius,
        };
        Sphere? head = twoSpheres
            ? new Sphere
            {
                Origin = new Vector3(0f, centerY, 0.875f),
                Radius = Radius,
            }
            : null;
        var transition = new Transition();
        transition.SpherePath.InitPath(
            begin: Vector3.Zero,
            end: Vector3.Zero,
            Cell,
            Radius,
            sphereHeight: twoSpheres ? 1.355f : 0f);
        transition.SpherePath.InsertType = InsertType.Placement;

        return flat is null
            ? BSPQuery.FindCollisions(
                root,
                resolved,
                transition,
                foot,
                head,
                foot.Origin,
                Vector3.UnitZ,
                1f)
            : FlatBspQuery.FindCollisions(
                flat,
                transition,
                foot,
                head,
                foot.Origin,
                Vector3.UnitZ,
                1f);
    }

    private static Transition MakeGroundedTransition(bool twoSpheres)
    {
        Vector3 current = new(2f, 3f, 4f);
        Vector3 target = current + new Vector3(0.1f, 0f, 0f);
        var transition = new Transition();
        transition.SpherePath.InitPath(
            current,
            target,
            Cell,
            Radius,
            sphereHeight: twoSpheres ? 1.835f : 0f);
        transition.SpherePath.SetCheckPos(target, Cell);
        transition.ObjectInfo.State =
            ObjectInfoState.Contact | ObjectInfoState.OnWalkable;
        transition.ObjectInfo.StepDown = true;
        transition.ObjectInfo.StepDownHeight = 0.04f;
        transition.ObjectInfo.StepUpHeight = 0.60f;
        transition.CollisionInfo.LastKnownContactPlane =
            new Plane(Vector3.UnitZ, 0f);
        transition.CollisionInfo.LastKnownContactPlaneValid = true;
        return transition;
    }

    private static void AssertVectorBits(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected.X),
            BitConverter.SingleToUInt32Bits(actual.X));
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected.Y),
            BitConverter.SingleToUInt32Bits(actual.Y));
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(expected.Z),
            BitConverter.SingleToUInt32Bits(actual.Z));
    }

    private static (
        PhysicsBSPNode Root,
        Dictionary<ushort, ResolvedPolygon> Resolved) BuildWall()
    {
        Vector3[] vertices =
        [
            new(-2f, 0f, -2f),
            new(-2f, 0f,  2f),
            new( 2f, 0f,  2f),
            new( 2f, 0f, -2f),
        ];
        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere
            {
                Origin = Vector3.Zero,
                Radius = 4f,
            },
        };
        root.Polygons.Add(1);
        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [1] = new ResolvedPolygon
            {
                Id = 1,
                Vertices = vertices,
                Plane = new Plane(Vector3.UnitY, 0f),
                NumPoints = vertices.Length,
                SidesType = CullMode.None,
            },
        };
        return (root, resolved);
    }
}
