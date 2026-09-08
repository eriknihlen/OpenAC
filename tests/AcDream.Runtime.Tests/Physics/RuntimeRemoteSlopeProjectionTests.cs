using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeRemoteSlopeProjectionTests
{
    private const float WalkableSlopeGradient = 0.6f;

    private const float RootMotionPerTick = 0.10f;

    private const int TrackedTicks = 30;

    private const float SurfaceTrackingToleranceMeters = 0.005f;

    private static Vector3 RampNormal(float gradient)
        => Vector3.Normalize(new Vector3(0f, gradient, 1f));

    [Fact]
    public void TheFixtureRampIsWalkableAndItsPlaneIsTheGeometricOne()
    {
        using RemoteRampHarness harness =
            RemoteRampHarness.OnRamp(WalkableSlopeGradient);

        Assert.True(harness.Remote.Body.OnWalkable);
        Assert.True(harness.Remote.Body.ContactPlaneValid);

        Vector3 expected = RampNormal(WalkableSlopeGradient);
        Vector3 actual = harness.Remote.Body.ContactPlane.Normal;
        Assert.True(
            Vector3.Distance(expected, actual) < 0.001f,
            $"contact plane normal was {actual}, expected the ramp's {expected}");
        Assert.True(actual.Z >= PhysicsGlobals.FloorZ);
    }

    [Fact]
    public void TheRemoteTickTracksTheSurfaceWhileRunningDownhill()
    {
        using RemoteRampHarness harness =
            RemoteRampHarness.OnRamp(WalkableSlopeGradient);
        AssertTracksSurface(harness);
    }

    /// <summary>
    /// Anti-vacuity guard for the two tests above: on flat ground they pass
    /// without Z ever having to move, so a fixture that quietly flattened
    /// would make them meaningless. This asserts the ramp genuinely forces a
    /// large Z excursion over the same number of ticks.
    /// </summary>
    [Fact]
    public void TheTrackingFixtureActuallyRequiresTheBodyToChangeZ()
    {
        using RemoteRampHarness harness =
            RemoteRampHarness.OnRamp(WalkableSlopeGradient);
        float startZ = harness.Remote.Body.Position.Z;

        harness.Tick(TrackedTicks, new Vector3(0f, RootMotionPerTick, 0f));

        float dz = harness.Remote.Body.Position.Z - startZ;
        Assert.True(
            dz < -1.0f,
            $"fixture is not exercising slope descent: dz = {dz:F4} m");
    }

    private static void AssertTracksSurface(RemoteRampHarness harness)
    {
        Assert.True(harness.Remote.Body.OnWalkable);

        // The settled resting offset between the body's root and the terrain
        // directly beneath it. Measured, not assumed: the spawn settle may put
        // the root a hair off the sampled surface.
        float restingOffset =
            harness.Remote.Body.Position.Z - harness.SurfaceZUnderBody();

        for (int tick = 1; tick <= TrackedTicks; tick++)
        {
            harness.Tick(1, new Vector3(0f, RootMotionPerTick, 0f));
            float offset =
                harness.Remote.Body.Position.Z - harness.SurfaceZUnderBody();
            Assert.True(
                MathF.Abs(offset - restingOffset) < SurfaceTrackingToleranceMeters,
                $"tick {tick}: body root sits {offset:F5} m above the terrain "
                + $"under it, expected {restingOffset:F5} m "
                + $"(pos {harness.Remote.Body.Position})");
        }
    }
}
