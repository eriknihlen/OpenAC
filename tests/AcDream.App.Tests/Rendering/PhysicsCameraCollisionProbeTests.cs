using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class PhysicsCameraCollisionProbeTests
{
    [Fact]
    public void SpherePathOffset_RoundTrips()
    {
        var p = new Vector3(10f, 20f, 30f);
        const float r = 0.3f;

        var path = PhysicsCameraCollisionProbe.ToSpherePath(p, r);
        Assert.Equal(p.Z - r, path.Z, 5);
        Assert.Equal(p.X, path.X, 5);
        Assert.Equal(p.Y, path.Y, 5);

        var back = PhysicsCameraCollisionProbe.FromSpherePath(path, r);
        Assert.Equal(p.X, back.X, 5);
        Assert.Equal(p.Y, back.Y, 5);
        Assert.Equal(p.Z, back.Z, 5);
    }

    // OpenAC #6b: the eye never goes below the water surface.
    [Theory]
    [InlineData(-3f, 0.3f)]   // under the surface: lifted to surface + radius
    [InlineData(0.1f, 0.3f)]  // just above but inside the margin: lifted
    [InlineData(2f, 2f)]      // clear of the water: untouched
    public void ClampAboveWater_LiftsAnEyeThatIsUnderTheSurface(float eyeZ, float expectedZ)
    {
        var eye = new Vector3(5f, 7f, eyeZ);

        var clamped = PhysicsCameraCollisionProbe.ClampAboveWater(eye, waterSurfaceZ: 0f, margin: 0.3f);

        Assert.Equal(5f, clamped.X);
        Assert.Equal(7f, clamped.Y);
        Assert.Equal(expectedZ, clamped.Z, 5);
    }

    [Fact]
    public void SweepEye_NoStartingCell_SnapsToPlayer()
    {
        var probe  = new PhysicsCameraCollisionProbe(new PhysicsEngine());
        var pivot  = new Vector3(0f, 0f, 1.5f);
        var eye    = new Vector3(-2f, 0f, 2.2f);
        var player = new Vector3(0f, 0f, 0f);

        var result = probe.SweepEye(pivot, eye, cellId: 0, selfEntityId: 0, playerPos: player);

        Assert.Equal(player, result.Eye);
        Assert.Equal(0u, result.ViewerCellId);
    }
}
