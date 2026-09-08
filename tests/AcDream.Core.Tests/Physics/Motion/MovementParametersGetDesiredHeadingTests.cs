using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MovementParametersGetDesiredHeadingTests
{
    [Theory]
    [InlineData(false, 0f)]   // RunForward, towards → 0
    [InlineData(true, 180f)]  // RunForward, away → 180
    public void RunForward_FourQuadrant(bool movingAway, float expected)
    {
        var p = new MovementParameters();
        float h = p.GetDesiredHeading(MotionCommand.RunForward, movingAway);
        Assert.Equal(expected, h);
    }

    [Theory]
    [InlineData(false, 0f)]   // WalkForward, towards → 0
    [InlineData(true, 180f)]  // WalkForward, away → 180
    public void WalkForward_FourQuadrant(bool movingAway, float expected)
    {
        var p = new MovementParameters();
        float h = p.GetDesiredHeading(MotionCommand.WalkForward, movingAway);
        Assert.Equal(expected, h);
    }

    [Theory]
    [InlineData(false, 180f)]  // WalkBackward, towards → 180 (face the target while backing up)
    [InlineData(true, 0f)]     // WalkBackward, away → 0
    public void WalkBackward_FourQuadrant(bool movingAway, float expected)
    {
        var p = new MovementParameters();
        float h = p.GetDesiredHeading(MotionCommand.WalkBackward, movingAway);
        Assert.Equal(expected, h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCommand_DefaultsToZero(bool movingAway)
    {
        var p = new MovementParameters();
        float h = p.GetDesiredHeading(MotionCommand.TurnRight, movingAway);
        Assert.Equal(0f, h);
    }

    [Fact]
    public void ZeroCommand_DefaultsToZero()
    {
        var p = new MovementParameters();
        Assert.Equal(0f, p.GetDesiredHeading(0u, movingAway: false));
        Assert.Equal(0f, p.GetDesiredHeading(0u, movingAway: true));
    }
}
