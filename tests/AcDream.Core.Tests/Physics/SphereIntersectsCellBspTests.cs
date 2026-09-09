using System.Numerics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class SphereIntersectsCellBspTests
{
    private static CellBSPNode SinglePlaneTree()
    {
        var leaf = new CellBSPNode { Type = BSPNodeType.Leaf };
        // Internal nodes don't set Type (default is non-Leaf). The production
        // PointInsideCellBsp / SphereIntersectsCellBsp both only branch on
        // `Type == Leaf` and otherwise treat the node as internal.
        return new CellBSPNode
        {
            SplittingPlane = new Plane(new Vector3(1f, 0f, 0f), 0f),  // x ≥ 0 is inside
            PosNode        = leaf,
        };
    }

    [Fact]
    public void NullRoot_ReturnsTrue()
    {
        Assert.True(BSPQuery.SphereIntersectsCellBsp(null, Vector3.Zero, 0.5f));
    }

    [Fact]
    public void Leaf_ReturnsTrue()
    {
        var leaf = new CellBSPNode { Type = BSPNodeType.Leaf };
        Assert.True(BSPQuery.SphereIntersectsCellBsp(leaf, Vector3.Zero, 0.5f));
    }

    [Fact]
    public void SphereCenterInsideHalfSpace_ReturnsTrue()
    {
        var root = SinglePlaneTree();
        Assert.True(BSPQuery.SphereIntersectsCellBsp(root, new Vector3(0.5f, 0f, 0f), 0.5f));
    }

    [Fact]
    public void SphereCenterOnPlane_ReturnsTrue()
    {
        var root = SinglePlaneTree();
        Assert.True(BSPQuery.SphereIntersectsCellBsp(root, new Vector3(0f, 0f, 0f), 0.5f));
    }

    [Fact]
    public void SphereCenterOutside_ButRadiusReachesIn_ReturnsTrue()
    {
        var root = SinglePlaneTree();
        Assert.True(BSPQuery.SphereIntersectsCellBsp(root, new Vector3(-0.3f, 0f, 0f), 0.5f));
    }

    [Fact]
    public void SphereFullyOutside_ReturnsFalse()
    {
        var root = SinglePlaneTree();
        Assert.False(BSPQuery.SphereIntersectsCellBsp(root, new Vector3(-1.0f, 0f, 0f), 0.5f));
    }

    [Fact]
    public void SphereTangentToPlane_ReturnsTrue()
    {
        var root = SinglePlaneTree();
        Assert.True(BSPQuery.SphereIntersectsCellBsp(root, new Vector3(-0.5f, 0f, 0f), 0.5f));
    }

    [Fact]
    public void PointInsideCellBsp_PointJustOutside_ReturnsFalse_ProvesRegression()
    {
        var root = SinglePlaneTree();
        Assert.False(BSPQuery.PointInsideCellBsp(root, new Vector3(-0.3f, 0f, 0f)));
    }
}
