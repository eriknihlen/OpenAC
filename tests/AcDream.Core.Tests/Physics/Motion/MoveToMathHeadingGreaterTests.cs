using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToMathHeadingGreaterTests
{
    private const uint TurnRight = MotionCommand.TurnRight;
    private const uint TurnLeft = MotionCommand.TurnLeft;

    [Fact]
    public void TurnRight_UnwrappedCase_SimpleGreater()
    {
        Assert.True(MoveToMath.HeadingGreater(90f, 30f, TurnRight));
        Assert.False(MoveToMath.HeadingGreater(30f, 90f, TurnRight));
    }

    [Fact]
    public void TurnRight_WrappedCase_ComparisonFlips()
    {
        // |350 - 10| = 340 > 180 → wrapped: greater = (b > a) = (10 > 350) = false
        Assert.False(MoveToMath.HeadingGreater(350f, 10f, TurnRight));
        // |10 - 350| = 340 > 180 → wrapped: greater = (b > a) = (350 > 10) = true
        Assert.True(MoveToMath.HeadingGreater(10f, 350f, TurnRight));
    }

    [Fact]
    public void TurnRight_ExactlyAt180Delta_UnwrappedBranch()
    {
        // fabs(a-b) > 180 is STRICT — exactly 180 uses the unwrapped branch.
        // a=200,b=20: fabs=180, not >180 → greater = (a>b) = true
        Assert.True(MoveToMath.HeadingGreater(200f, 20f, TurnRight));
    }

    [Fact]
    public void TurnLeft_InvertsTheUnwrappedResult()
    {
        Assert.False(MoveToMath.HeadingGreater(90f, 30f, TurnLeft));
        Assert.True(MoveToMath.HeadingGreater(30f, 90f, TurnLeft));
    }

    [Fact]
    public void TurnLeft_InvertsTheWrappedResult()
    {
        Assert.True(MoveToMath.HeadingGreater(350f, 10f, TurnLeft));
        Assert.False(MoveToMath.HeadingGreater(10f, 350f, TurnLeft));
    }

    [Fact]
    public void EqualHeadings_NotGreater_TurnRight()
    {
        Assert.False(MoveToMath.HeadingGreater(45f, 45f, TurnRight));
    }

    [Fact]
    public void EqualHeadings_InvertedToTrue_TurnLeft()
    {
        // greater=false for equal headings; TurnLeft inverts → true.
        Assert.True(MoveToMath.HeadingGreater(45f, 45f, TurnLeft));
    }

    [Fact]
    public void AnyNonTurnRightCommand_AlsoInverts()
    {
        Assert.False(MoveToMath.HeadingGreater(90f, 30f, 0u));
        Assert.False(MoveToMath.HeadingGreater(90f, 30f, MotionCommand.WalkForward));
    }
}
