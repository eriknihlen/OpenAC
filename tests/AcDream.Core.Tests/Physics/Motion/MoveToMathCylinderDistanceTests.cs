using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToMathCylinderDistanceTests
{
    [Fact]
    public void TwoCylinders_HorizontallySeparated_SubtractsBothRadii()
    {
        float d = MoveToMath.CylinderDistance(
            ownRadius: 1f, ownHeight: 2f, ownPos: Vector3.Zero,
            targetRadius: 2f, targetHeight: 2f, targetPos: new Vector3(10f, 0f, 0f));

        Assert.Equal(7f, d, 3);
    }

    [Fact]
    public void TwoCylinders_Overlapping_ReturnsSignedThreeDimensionalOverlap()
    {
        // radial gap = 1 - 10 = -9; vertical gap = 0 - 2 = -2.
        float d = MoveToMath.CylinderDistance(
            ownRadius: 5f, ownHeight: 2f, ownPos: Vector3.Zero,
            targetRadius: 5f, targetHeight: 2f, targetPos: new Vector3(1f, 0f, 0f));

        Assert.Equal(-MathF.Sqrt(85f), d, 3);
    }

    [Fact]
    public void TwoCylinders_ZeroRadii_ReducesToCenterDistance()
    {
        float d = MoveToMath.CylinderDistance(
            ownRadius: 0f, ownHeight: 2f, ownPos: Vector3.Zero,
            targetRadius: 0f, targetHeight: 2f, targetPos: new Vector3(3f, 4f, 0f));

        Assert.Equal(5f, d, 3);   // 3-4-5 triangle
    }

    [Fact]
    public void TwoCylinders_VerticallyAndRadiallySeparated_CombinesBothGaps()
    {
        float d = MoveToMath.CylinderDistance(
            ownRadius: 1f, ownHeight: 2f, ownPos: Vector3.Zero,
            targetRadius: 1f, targetHeight: 2f, targetPos: new Vector3(3f, 0f, 4f));

        Assert.Equal(MathF.Sqrt(13f), d, 3);
    }

    [Fact]
    public void SamePosition_ReturnsSignedOverlap()
    {
        float d = MoveToMath.CylinderDistance(
            ownRadius: 0.5f, ownHeight: 2f, ownPos: Vector3.Zero,
            targetRadius: 0.5f, targetHeight: 2f, targetPos: Vector3.Zero);

        Assert.Equal(-MathF.Sqrt(5f), d, 3);
    }

    [Fact]
    public void OppositeGapSigns_ReturnsRadialGapVerbatim()
    {
        float d = MoveToMath.CylinderDistance(
            ownRadius: 5f, ownHeight: 1f, ownPos: Vector3.Zero,
            targetRadius: 5f, targetHeight: 1f, targetPos: new Vector3(0f, 0f, 3f));

        Assert.Equal(-7f, d, 3);
    }
}
