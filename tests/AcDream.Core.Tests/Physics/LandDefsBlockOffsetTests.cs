using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class LandDefsBlockOffsetTests
{

    [Fact]
    public void SameLandblock_ReturnsZero()
    {
        // both 0xC95B
        Assert.Equal(Vector3.Zero, LandDefs.GetBlockOffset(0xC95B0001u, 0xC95B0008u));
    }

    [Fact]
    public void SouthNeighbour_IsMinus192Y()
    {
        Assert.Equal(new Vector3(0f, -192f, 0f), LandDefs.GetBlockOffset(0xC95B0001u, 0xC95A0001u));
    }

    [Fact]
    public void EastNeighbour_IsPlus192X()
    {
        // 0xC95B (lbX=0xC9) → 0xCA5B (lbX=0xCA): one landblock east = (+192,0,0).
        Assert.Equal(new Vector3(192f, 0f, 0f), LandDefs.GetBlockOffset(0xC95B0001u, 0xCA5B0001u));
    }

    [Fact]
    public void DiagonalNeighbour_CombinesBothAxes()
    {
        // 0xC95B → 0xCA5C: lbX +1 (+192), lbY +1 (+192).
        Assert.Equal(new Vector3(192f, 192f, 0f), LandDefs.GetBlockOffset(0xC95B0001u, 0xCA5C0001u));
    }
}
