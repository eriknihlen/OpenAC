using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class ObjectRangeMathTests
{
    [Fact]
    public void ExternalContainerRange_UsesSurfaceGapRatherThanCenterDistance()
    {
        var player = new Vector3(7.01f, 98.06f, -0.45f);
        var corpse = new Vector3(3.73f, 98.03f, -0.44f);

        bool inRange = ObjectRangeMath.ObjectsInRange(
            player, firstRadius: 0.48f, firstHeight: 1.835f,
            corpse, secondRadius: 0.80f, secondHeight: 0.50f,
            range: 2.01,
            useRadii: true,
            ignoreZDelta: false);

        Assert.True(inRange);
    }

    [Fact]
    public void CenterDistanceMode_DoesNotSubtractObjectRadii()
    {
        bool inRange = ObjectRangeMath.ObjectsInRange(
            Vector3.Zero, firstRadius: 10f, firstHeight: 10f,
            new Vector3(3f, 0f, 0f), secondRadius: 10f, secondHeight: 10f,
            range: 2.0,
            useRadii: false,
            ignoreZDelta: false);

        Assert.False(inRange);
    }

    [Fact]
    public void IgnoreZDelta_UsesXyCenterDistanceAndTakesPrecedenceOverRadii()
    {
        bool inRange = ObjectRangeMath.ObjectsInRange(
            Vector3.Zero, firstRadius: 100f, firstHeight: 100f,
            new Vector3(3f, 4f, 1000f), secondRadius: 100f, secondHeight: 100f,
            range: 5.0,
            useRadii: true,
            ignoreZDelta: true);

        Assert.True(inRange);
    }

    [Fact]
    public void RangeBoundary_IsInclusive()
    {
        bool inRange = ObjectRangeMath.ObjectsInRange(
            Vector3.Zero, firstRadius: 0f, firstHeight: 0f,
            new Vector3(3f, 4f, 0f), secondRadius: 0f, secondHeight: 0f,
            range: 5.0,
            useRadii: false,
            ignoreZDelta: false);

        Assert.True(inRange);
    }
}
