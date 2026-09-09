using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementParametersGetCommandTests
{

    [Fact]
    public void PlainTowards_DistGreaterThanDto_WalkForward_NotMovingAway()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f };
        // move_towards=true (default), move_away=false (default)

        p.GetCommand(dist: 5f, headingDiff: 0f, out uint motion, out HoldKey holdKey, out bool movingAway);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.False(movingAway);
    }

    [Fact]
    public void PlainTowards_DistNotGreaterThanDto_Idle()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f };

        p.GetCommand(dist: 0.6f, headingDiff: 0f, out uint motion, out _, out bool movingAway);

        Assert.Equal(0u, motion);
        Assert.False(movingAway);
    }

    [Fact]
    public void PlainTowards_DistLessThanDto_Idle()
    {
        var p = new MovementParameters { DistanceToObject = 0.6f };

        p.GetCommand(dist: 0.1f, headingDiff: 0f, out uint motion, out _, out _);

        Assert.Equal(0u, motion);
    }


    [Fact]
    public void PureAway_DistLessThanMinDistance_WalkForward_MovingAway()
    {
        var p = new MovementParameters
        {
            MoveTowards = false,
            MoveAway = true,
            MinDistance = 5f,
        };

        p.GetCommand(dist: 2f, headingDiff: 0f, out uint motion, out _, out bool movingAway);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.True(movingAway);
    }

    [Fact]
    public void PureAway_DistNotLessThanMinDistance_Idle()
    {
        var p = new MovementParameters
        {
            MoveTowards = false,
            MoveAway = true,
            MinDistance = 5f,
        };

        p.GetCommand(dist: 5f, headingDiff: 0f, out uint motion, out _, out bool movingAway);

        Assert.Equal(0u, motion);
        Assert.False(movingAway);
    }

    // ── towards_and_away delegate (both move_towards AND move_away set) ───

    [Fact]
    public void TowardsAndAway_DistGreaterThanDto_DelegatesToWalkForwardTowards()
    {
        var p = new MovementParameters
        {
            MoveTowards = true,
            MoveAway = true,
            DistanceToObject = 0.6f,
            MinDistance = 0.2f,
        };

        p.GetCommand(dist: 5f, headingDiff: 0f, out uint motion, out _, out bool movingAway);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.False(movingAway);
    }

    [Fact]
    public void TowardsAndAway_InsideMinBand_WalkBackwards_MovingAway()
    {
        var p = new MovementParameters
        {
            MoveTowards = true,
            MoveAway = true,
            DistanceToObject = 0.6f,
            MinDistance = 0.2f,
        };

        // dist - min_distance < epsilon → inside the min band
        p.GetCommand(dist: 0.2f, headingDiff: 0f, out uint motion, out _, out bool movingAway);

        Assert.Equal(MotionCommand.WalkBackward, motion);
        Assert.True(movingAway);
    }

    [Fact]
    public void TowardsAndAway_InsideDeadband_Idle()
    {
        var p = new MovementParameters
        {
            MoveTowards = true,
            MoveAway = true,
            DistanceToObject = 0.6f,
            MinDistance = 0.2f,
        };

        // strictly inside [min, dto] — neither band fires
        p.GetCommand(dist: 0.4f, headingDiff: 0f, out uint motion, out _, out _);

        Assert.Equal(0u, motion);
    }


    [Fact]
    public void NeitherTowardsNorAway_FallsToPlainTowardsBranch()
    {
        var p = new MovementParameters
        {
            MoveTowards = false,
            MoveAway = false,
            DistanceToObject = 0.6f,
        };

        p.GetCommand(dist: 5f, headingDiff: 0f, out uint motion, out _, out bool movingAway);

        Assert.Equal(MotionCommand.WalkForward, motion);
        Assert.False(movingAway);
    }


    [Fact]
    public void HoldKey_CanChargeSet_AlwaysRun_FastPath()
    {
        var p = new MovementParameters
        {
            CanCharge = true,
            CanRun = false,
            CanWalk = true,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        p.GetCommand(dist: 0.6f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.Run, holdKey);
    }

    [Fact]
    public void HoldKey_CanRunClear_AlwaysWalk_RegardlessOfDistance()
    {
        var p = new MovementParameters
        {
            CanCharge = false,
            CanRun = false,
            CanWalk = true,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        p.GetCommand(dist: 1000f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.None, holdKey);
    }

    [Fact]
    public void HoldKey_CanRunSet_CanWalkClear_AlwaysRun_WalkIncapable()
    {
        var p = new MovementParameters
        {
            CanCharge = false,
            CanRun = true,
            CanWalk = false,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        p.GetCommand(dist: 0.6f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.Run, holdKey);
    }

    [Fact]
    public void HoldKey_CanRunAndCanWalk_WithinThreshold_Walk()
    {
        var p = new MovementParameters
        {
            CanCharge = false,
            CanRun = true,
            CanWalk = true,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        // dist - dto = 10 <= 15 → walk
        p.GetCommand(dist: 10.6f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.None, holdKey);
    }

    [Fact]
    public void HoldKey_CanRunAndCanWalk_BeyondThreshold_Run()
    {
        var p = new MovementParameters
        {
            CanCharge = false,
            CanRun = true,
            CanWalk = true,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        // dist - dto = 15.1 > 15 → run
        p.GetCommand(dist: 15.7f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.Run, holdKey);
    }

    [Fact]
    public void HoldKey_ThresholdEdge_ExactlyAtThreshold_IsInclusive_Walk()
    {
        var p = new MovementParameters
        {
            CanCharge = false,
            CanRun = true,
            CanWalk = true,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        // dist - dto = exactly 15.0
        p.GetCommand(dist: 15.6f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.None, holdKey);
    }

    [Fact]
    public void HoldKey_ThresholdEdge_JustOverThreshold_Run()
    {
        var p = new MovementParameters
        {
            CanCharge = false,
            CanRun = true,
            CanWalk = true,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        // dist - dto = 15.0 + epsilon
        p.GetCommand(dist: 15.600001f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.Run, holdKey);
    }

    [Fact]
    public void HoldKey_CanChargeSet_OverridesWalkIncapableAndThreshold()
    {
        var p = new MovementParameters
        {
            CanCharge = true,
            CanRun = true,
            CanWalk = true,
            WalkRunThreshhold = 1000f,   // would otherwise force walk
            DistanceToObject = 0.6f,
        };

        p.GetCommand(dist: 0.6f, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(HoldKey.Run, holdKey);
    }


    [Theory]
    [InlineData(true, true, false, false, HoldKey.None)]
    [InlineData(true, true, false, true, HoldKey.Run)]
    [InlineData(true, false, false, false, HoldKey.Run)]
    [InlineData(true, false, false, true, HoldKey.Run)]    // run-only, far → run
    [InlineData(false, true, false, false, HoldKey.None)]  // walk-only → always walk
    [InlineData(false, true, false, true, HoldKey.None)]   // walk-only, far → still walk
    [InlineData(false, false, false, false, HoldKey.None)]
    [InlineData(false, false, true, false, HoldKey.Run)]
    public void HoldKey_FourCapabilityQuadrants_MatchRetailCascade(
        bool canRun, bool canWalk, bool canCharge, bool distBeyondThreshold, HoldKey expected)
    {
        var p = new MovementParameters
        {
            CanRun = canRun,
            CanWalk = canWalk,
            CanCharge = canCharge,
            WalkRunThreshhold = 15f,
            DistanceToObject = 0.6f,
        };

        float dist = distBeyondThreshold ? 20f : 5f;   // 20-0.6=19.4>15 ; 5-0.6=4.4<=15
        p.GetCommand(dist, headingDiff: 0f, out _, out HoldKey holdKey, out _);

        Assert.Equal(expected, holdKey);
    }
}
