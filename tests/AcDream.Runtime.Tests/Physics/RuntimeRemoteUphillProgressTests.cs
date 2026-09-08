using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimeRemoteUphillProgressTests
{
    private const float WalkableSlopeGradient = 0.6f;

    private const int TrackedTicks = 30;

    private static readonly Vector3 UphillRootMotionPerTick =
        new(0.02588f, -0.09659f, 0f);

    private static readonly Vector3 ExactlyUpSlopeRootMotionPerTick =
        new(0f, -0.1f, 0f);

    /// <summary>
    /// Same band the downhill tracking test uses. Measured drift on this
    /// fixture is under 1e-4 m.
    /// </summary>
    private const float SurfaceTrackingToleranceMeters = 0.005f;

    [Fact]
    public void ARemoteWithABodyClimbsAWalkableSlopeAndKeepsItsFeetOnIt()
    {
        using RemoteRampHarness harness =
            RemoteRampHarness.OnRamp(WalkableSlopeGradient);
        PhysicsBody body = harness.Remote.Body;

        Assert.True(body.OnWalkable);

        harness.Tick(1, UphillRootMotionPerTick);
        Assert.True(
            (body.TransientState & TransientStateFlags.Sliding) == 0,
            "the landing sliding latch survived its first off-gradient step");

        float startZ = body.Position.Z;
        float previousZ = startZ;

        // The settled resting offset between the body's root and the terrain
        // directly beneath it. Measured, not assumed.
        float restingOffset = body.Position.Z - harness.SurfaceZUnderBody();

        for (int tick = 1; tick <= TrackedTicks; tick++)
        {
            harness.Tick(1, UphillRootMotionPerTick);

            Assert.True(
                body.Position.Z > previousZ,
                $"tick {tick}: body gained no height running uphill "
                + $"(z {previousZ:F5} -> {body.Position.Z:F5}, pos {body.Position})");

            float offset = body.Position.Z - harness.SurfaceZUnderBody();
            Assert.True(
                MathF.Abs(offset - restingOffset) < SurfaceTrackingToleranceMeters,
                $"tick {tick}: body root sits {offset:F5} m above the terrain "
                + $"under it, expected {restingOffset:F5} m (pos {body.Position})");

            previousZ = body.Position.Z;
        }

        float ascent = body.Position.Z - startZ;
        Assert.True(
            ascent > 1.0f,
            $"fixture is not exercising slope ascent: dz = {ascent:F4} m");
    }

    [Fact]
    public void AnExactlyUpSlopeOffsetIsAbsorbedByThePersistedSlidingNormal()
    {
        using RemoteRampHarness harness =
            RemoteRampHarness.OnRamp(WalkableSlopeGradient);
        PhysicsBody body = harness.Remote.Body;

        Assert.True((body.TransientState & TransientStateFlags.Sliding) != 0);
        Assert.True(
            Vector3.Distance(body.SlidingNormal, new Vector3(0f, 1f, 0f)) < 0.001f,
            $"expected the flattened ramp normal, got {body.SlidingNormal}");

        Vector3 latched = body.Position;

        harness.Tick(5, ExactlyUpSlopeRootMotionPerTick);

        Assert.Equal(latched.X, body.Position.X);
        Assert.Equal(latched.Y, body.Position.Y);
        float expectedLiftedZ = latched.Z + 0.48f * (1f / 0.857493f - 1f);
        Assert.Equal(expectedLiftedZ, body.Position.Z, 4);
        Assert.True((body.TransientState & TransientStateFlags.Sliding) != 0);

        harness.Tick(1, UphillRootMotionPerTick);

        Assert.True((body.TransientState & TransientStateFlags.Sliding) == 0);
        Assert.True(
            body.Position.X > latched.X,
            $"the cross-slope component was absorbed too (pos {body.Position})");

        Assert.Equal(latched.Z, body.Position.Z, 4);

        harness.Tick(1, UphillRootMotionPerTick);

        Assert.True(
            body.Position.Z > latched.Z,
            $"body did not climb once the latch cleared (pos {body.Position})");
    }
}
