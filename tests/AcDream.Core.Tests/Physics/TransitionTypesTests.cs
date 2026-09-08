using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

public class TransitionTypesTests
{
    [Fact]
    public void TryFindIndoorWalkablePlane_TwoOverlappingFloors_PicksClosestBelowFoot_PreservesAllowance()
    {

        var cellPhysics = BuildTwoFloorCellPhysics(lowerZ: 0f, upperZ: 3f);

        var transition = new Transition();
        const float sentinelAllowance = 0.42f;
        transition.SpherePath.WalkableAllowance = sentinelAllowance;
        transition.SpherePath.WalkInterp = 1.0f;

        bool found = transition.TryFindIndoorWalkablePlane(
            cellPhysics,
            localFootCenter: new Vector3(0.5f, 0.5f, 0.4f),
            sphereRadius: 0.48f,
            out var worldPlane,
            out var worldVertices,
            out var hitPolyId);

        Assert.True(found);
        // Lower polygon's local plane Normal.Z = 1.0; identity world transform
        // means world Normal.Z is also 1.0.
        Assert.Equal(1.0f, worldPlane.Normal.Z, precision: 3);
        // World vertices match the lower polygon (Z=0 in world space, identity transform).
        Assert.Equal(4, worldVertices.Length);
        Assert.Equal(0f, worldVertices[0].Z, precision: 3);
        // hitPolyId is the dictionary key — lower polygon was inserted as key 0.
        Assert.Equal(0u, hitPolyId);
        // WalkableAllowance must be restored to the sentinel.
        Assert.Equal(sentinelAllowance, transition.SpherePath.WalkableAllowance);
    }

    [Fact]
    public void TryFindIndoorWalkablePlane_FlatOnlyMatchesGraphResult()
    {
        CellPhysics graph = BuildTwoFloorCellPhysics(
            lowerZ: 0f,
            upperZ: 3f);
        FlatPhysicsBsp flat = FlatCollisionAssetBuilder.FlattenPhysicsBsp(
            graph.BSP!.Root,
            graph.Resolved);
        var flatOnly = new CellPhysics
        {
            WorldTransform = graph.WorldTransform,
            InverseWorldTransform = graph.InverseWorldTransform,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            FlatPhysicsBsp = flat,
        };
        var graphTransition = new Transition();
        var flatTransition = new Transition();
        graphTransition.SpherePath.WalkInterp = 1f;
        flatTransition.SpherePath.WalkInterp = 1f;
        Vector3 foot = new(0.5f, 0.5f, 0.4f);

        bool graphFound = graphTransition.TryFindIndoorWalkablePlane(
            graph,
            foot,
            0.48f,
            out Plane graphPlane,
            out Vector3[] graphVertices,
            out uint graphId);
        bool flatFound = flatTransition.TryFindIndoorWalkablePlane(
            flatOnly,
            foot,
            0.48f,
            out Plane flatPlane,
            out Vector3[] flatVertices,
            out uint flatId);

        Assert.Equal(graphFound, flatFound);
        Assert.Equal(graphId, flatId);
        Assert.Equal(
            BitConverter.SingleToInt32Bits(graphPlane.Normal.X),
            BitConverter.SingleToInt32Bits(flatPlane.Normal.X));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(graphPlane.Normal.Y),
            BitConverter.SingleToInt32Bits(flatPlane.Normal.Y));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(graphPlane.Normal.Z),
            BitConverter.SingleToInt32Bits(flatPlane.Normal.Z));
        Assert.Equal(
            BitConverter.SingleToInt32Bits(graphPlane.D),
            BitConverter.SingleToInt32Bits(flatPlane.D));
        Assert.Equal(graphVertices, flatVertices);
    }

    private static CellPhysics BuildTwoFloorCellPhysics(float lowerZ, float upperZ)
    {
        Vector3[] lowerVerts =
        {
            new Vector3(0f, 0f, lowerZ),
            new Vector3(1f, 0f, lowerZ),
            new Vector3(1f, 1f, lowerZ),
            new Vector3(0f, 1f, lowerZ),
        };
        Vector3[] upperVerts =
        {
            new Vector3(0f, 0f, upperZ),
            new Vector3(1f, 0f, upperZ),
            new Vector3(1f, 1f, upperZ),
            new Vector3(0f, 1f, upperZ),
        };

        var resolved = new Dictionary<ushort, ResolvedPolygon>
        {
            [0] = new ResolvedPolygon
            {
                Vertices = lowerVerts,
                Plane = new Plane(Vector3.UnitZ, -lowerZ),
                NumPoints = 4,
                SidesType = CullMode.None,
                Id = 0,
            },
            [1] = new ResolvedPolygon
            {
                Vertices = upperVerts,
                Plane = new Plane(Vector3.UnitZ, -upperZ),
                NumPoints = 4,
                SidesType = CullMode.None,
                Id = 1,
            },
        };

        var center = new Vector3(0.5f, 0.5f, (lowerZ + upperZ) * 0.5f);
        float halfHeight = MathF.Abs(upperZ - lowerZ) * 0.5f + 1.0f;
        float radius = MathF.Sqrt(0.5f * 0.5f + 0.5f * 0.5f + halfHeight * halfHeight);

        var root = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = center, Radius = radius },
        };
        root.Polygons.Add(0);
        root.Polygons.Add(1);

        var bsp = new PhysicsBSPTree { Root = root };

        return new CellPhysics
        {
            BSP = bsp,
            Resolved = resolved,
            WorldTransform = Matrix4x4.Identity,
            InverseWorldTransform = Matrix4x4.Identity,
        };
    }
}
