using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MoveToMathHeadingDiffTests
{
    private const float Eps = 0.000199999995f;
    private const uint TurnRight = MotionCommand.TurnRight;
    private const uint TurnLeft = MotionCommand.TurnLeft;

    // ── basic subtraction, TurnRight (no mirror) ───────────────────────────

    [Fact]
    public void TurnRight_SimplePositiveDiff_NoWrap()
    {
        float d = MoveToMath.HeadingDiff(90f, 30f, TurnRight);
        Assert.Equal(60f, d, 3);
    }

    [Fact]
    public void TurnRight_NegativeDiff_WrapsBy360()
    {
        // h1 - h2 = 30 - 90 = -60 → wraps to 300
        float d = MoveToMath.HeadingDiff(30f, 90f, TurnRight);
        Assert.Equal(300f, d, 3);
    }

    [Fact]
    public void TurnRight_ZeroDiff_IsZero()
    {
        float d = MoveToMath.HeadingDiff(45f, 45f, TurnRight);
        Assert.Equal(0f, d, 3);
    }

    // ── epsilon boundary (both sides) ──────────────────────────────────────

    [Fact]
    public void EpsilonBoundary_ExactlyAtEpsilon_NotSnappedToZero()
    {
        // fabs(d) < EPSILON is a STRICT less-than — exactly at epsilon does
        // NOT snap to zero.
        float d = MoveToMath.HeadingDiff(Eps, 0f, TurnRight);
        Assert.NotEqual(0f, d);
        Assert.Equal(Eps, d, 6);
    }

    [Fact]
    public void EpsilonBoundary_JustBelowEpsilon_SnapsToZero()
    {
        float d = MoveToMath.HeadingDiff(Eps * 0.5f, 0f, TurnRight);
        Assert.Equal(0f, d);
    }

    [Fact]
    public void EpsilonBoundary_NegativeJustBelowNegEpsilon_WrapsBy360()
    {
        // d = -Eps * 1.5 < -Eps → wraps
        float raw = -Eps * 1.5f;
        float d = MoveToMath.HeadingDiff(raw, 0f, TurnRight);
        Assert.Equal(raw + 360f, d, 3);
    }

    [Fact]
    public void EpsilonBoundary_NegativeExactlyAtNegEpsilon_DoesNotWrap()
    {
        // d < -EPSILON is STRICT — exactly -EPSILON does not wrap.
        float d = MoveToMath.HeadingDiff(-Eps, 0f, TurnRight);
        Assert.Equal(-Eps, d, 6);
    }

    // ── the mirror (TurnLeft / not-TurnRight) ──────────────────────────────

    [Fact]
    public void TurnLeft_PositiveDiffAboveEpsilon_MirroredTo360MinusD()
    {
        float d = MoveToMath.HeadingDiff(90f, 30f, TurnLeft);
        // raw = 60; mirror: 360 - 60 = 300
        Assert.Equal(300f, d, 3);
    }

    [Fact]
    public void TurnLeft_NegativeDiff_WrapsThenMirrors()
    {
        // h1-h2 = 30-90 = -60 → wraps to 300 → mirror gate (300 > EPS, not
        // TurnRight) → 360 - 300 = 60
        float d = MoveToMath.HeadingDiff(30f, 90f, TurnLeft);
        Assert.Equal(60f, d, 3);
    }

    [Fact]
    public void TurnLeft_ZeroDiff_MirrorGateDoesNotFire_StaysZero()
    {
        float d = MoveToMath.HeadingDiff(45f, 45f, TurnLeft);
        Assert.Equal(0f, d);
    }

    [Fact]
    public void TurnLeft_AtEpsilonBoundary_MirrorGateIsStrictGreaterThan()
    {
        // d > EPSILON is STRICT: exactly at EPSILON does NOT mirror.
        float d = MoveToMath.HeadingDiff(Eps, 0f, TurnLeft);
        Assert.Equal(Eps, d, 6);
    }

    [Fact]
    public void TurnLeft_JustAboveEpsilon_Mirrors()
    {
        float raw = Eps * 2f;
        float d = MoveToMath.HeadingDiff(raw, 0f, TurnLeft);
        Assert.Equal(360f - raw, d, 3);
    }

    [Fact]
    public void AnyNonTurnRightCommand_AlsoMirrors()
    {
        float d = MoveToMath.HeadingDiff(90f, 30f, 0u);
        Assert.Equal(300f, d, 3);

        float d2 = MoveToMath.HeadingDiff(90f, 30f, MotionCommand.WalkForward);
        Assert.Equal(300f, d2, 3);
    }


    [Fact]
    public void TurnRight_FullCircleInputs_NormalizeCorrectly()
    {
        float d = MoveToMath.HeadingDiff(350f, 10f, TurnRight);
        Assert.Equal(340f, d, 3);
    }

    [Fact]
    public void TurnLeft_FullCircleInputs_MirroredAfterNormalize()
    {
        // raw = 350-10 = 340 (no wrap needed, positive); mirror: 360-340=20
        float d = MoveToMath.HeadingDiff(350f, 10f, TurnLeft);
        Assert.Equal(20f, d, 3);
    }
}
