using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PortalPlaneTests
{
    // Helper: build an XY-plane portal (z = 0) from three known vertices.
    private static PortalPlane XyPlane() =>
        PortalPlane.FromVertices(
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            targetCellId: 42,
            ownerCellId: 7,
            flags: 0);

    [Fact]
    public void FromVertices_ComputesCorrectNormal()
    {
        var plane = XyPlane();

        Assert.Equal(0f, plane.Normal.X, precision: 5);
        Assert.Equal(0f, plane.Normal.Y, precision: 5);
        Assert.Equal(1f, MathF.Abs(plane.Normal.Z), precision: 5);
    }

    [Fact]
    public void IsCrossing_PositionsOnOppositeSides_ReturnsTrue()
    {
        var plane = XyPlane(); // normal is (0,0,±1), D = 0

        // One position above the XY plane, one below.
        var above = new Vector3(0, 0, 1f);
        var below = new Vector3(0, 0, -1f);

        Assert.True(plane.IsCrossing(above, below));
        Assert.True(plane.IsCrossing(below, above));
    }

    [Fact]
    public void IsCrossing_PositionsOnSameSide_ReturnsFalse()
    {
        var plane = XyPlane();

        var pos1 = new Vector3(0, 0, 1f);
        var pos2 = new Vector3(0, 0, 2f);

        Assert.False(plane.IsCrossing(pos1, pos2));
    }

    [Fact]
    public void IsCrossing_StartOnPlane_ReturnsFalse()
    {
        var plane = XyPlane();

        var onPlane = new Vector3(0.5f, 0.5f, 0f);
        var above   = new Vector3(0.5f, 0.5f, 1f);

        Assert.False(plane.IsCrossing(onPlane, above));
    }
}
