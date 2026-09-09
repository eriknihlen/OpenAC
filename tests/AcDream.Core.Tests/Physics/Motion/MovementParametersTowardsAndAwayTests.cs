using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementParametersTowardsAndAwayTests
{
    [Fact]
    public void DistGreaterThanDto_WalkForward_Towards()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f, MinDistance = 0.2f };

        p.TowardsAndAway(dist: 5f, out uint cmd, out bool movingAway);

        Assert.Equal(MotionCommand.WalkForward, cmd);
        Assert.False(movingAway);
    }

    [Fact]
    public void DistExactlyAtDto_NotGreater_FallsToMinBandCheck()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f, MinDistance = 0.2f };

        // dist == dto is NOT > dto, so falls through to the min-band test;
        // 0.6 - 0.2 = 0.4, not < epsilon → idle.
        p.TowardsAndAway(dist: 0.6f, out uint cmd, out _);

        Assert.Equal(0u, cmd);
    }

    [Fact]
    public void InsideMinDistanceEpsilonBand_WalkBackwards_Away()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f, MinDistance = 0.2f };

        // dist - min_distance < 0.000199999995f
        p.TowardsAndAway(dist: 0.2f, out uint cmd, out bool movingAway);

        Assert.Equal(MotionCommand.WalkBackward, cmd);
        Assert.True(movingAway);
    }

    [Fact]
    public void InsideMinDistanceEpsilonBand_JustBelowEpsilon_StillWalkBackwards()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f, MinDistance = 0.2f };

        p.TowardsAndAway(dist: 0.2f + 0.0001f, out uint cmd, out bool movingAway);

        Assert.Equal(MotionCommand.WalkBackward, cmd);
        Assert.True(movingAway);
    }

    [Fact]
    public void StrictlyBetweenMinAndDto_Idle()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f, MinDistance = 0.2f };

        p.TowardsAndAway(dist: 0.4f, out uint cmd, out bool movingAway);

        Assert.Equal(0u, cmd);
        Assert.False(movingAway);
    }

    [Fact]
    public void JustOutsideMinBand_NotYetIdle_Idle()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f, MinDistance = 0.2f };

        // dist - min = 0.0003, just over epsilon (0.0002) → NOT in the min band → idle
        p.TowardsAndAway(dist: 0.2003f, out uint cmd, out _);

        Assert.Equal(0u, cmd);
    }
}
