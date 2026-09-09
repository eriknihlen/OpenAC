using System.Numerics;
using AcDream.Core.Physics.Motion;
using DatReaderWriter.Types;

namespace AcDream.Core.Tests.Physics.Motion;

public sealed class MotionDeltaFrameTests
{
    [Fact]
    public void Combine_MatchesRetailFrameOps_ForTranslationAndOrientation()
    {
        Quaternion baseRotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 2f);
        Quaternion deltaRotation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ,
            MathF.PI / 4f);

        var expected = new Frame
        {
            Origin = new Vector3(10f, 20f, 30f),
            Orientation = baseRotation,
        };
        FrameOps.Combine(expected, new Frame
        {
            Origin = new Vector3(2f, 0f, 1f),
            Orientation = deltaRotation,
        });

        var actual = new MotionDeltaFrame
        {
            Origin = new Vector3(10f, 20f, 30f),
            Orientation = baseRotation,
        };
        actual.Combine(
            new Vector3(2f, 0f, 1f),
            deltaRotation);

        AssertVectorEqual(expected.Origin, actual.Origin);
        AssertQuaternionEquivalent(expected.Orientation, actual.Orientation);
    }

    [Fact]
    public void Combine_SequentialFrames_RotatesLaterTranslationByEarlierTurn()
    {
        var accumulated = new MotionDeltaFrame();
        accumulated.Combine(
            Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
        accumulated.Combine(
            new Vector3(0f, 2f, 0f),
            Quaternion.Identity);

        AssertVectorEqual(new Vector3(-2f, 0f, 0f), accumulated.Origin);
        AssertQuaternionEquivalent(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            accumulated.Orientation);
    }

    [Fact]
    public void Combine_NonCommutingRotations_UsesRetailPostMultiplyOrder()
    {
        Quaternion aroundX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
        Quaternion aroundY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.4f);
        var accumulated = new MotionDeltaFrame
        {
            Orientation = aroundX,
        };

        accumulated.Combine(Vector3.Zero, aroundY);

        AssertQuaternionEquivalent(
            FrameOps.SetRotate(Vector3.Zero, aroundX, aroundX * aroundY),
            accumulated.Orientation);
        Assert.True(MathF.Abs(Quaternion.Dot(
            accumulated.Orientation,
            Quaternion.Normalize(aroundY * aroundX))) < 0.999f);
    }

    [Theory]
    [MemberData(nameof(InvalidRotations))]
    public void Combine_InvalidCandidateRotation_RestoresPreviousQuaternion(
        Quaternion invalid)
    {
        Quaternion previous = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.35f);
        var accumulated = new MotionDeltaFrame
        {
            Orientation = previous,
        };

        accumulated.Combine(Vector3.Zero, invalid);

        AssertQuaternionEquivalent(previous, accumulated.Orientation);
    }

    [Fact]
    public void Combine_NearZeroNonzeroQuaternion_NormalizesLikeRetail()
    {
        var accumulated = new MotionDeltaFrame();
        var tiny = new Quaternion(1e-30f, 0f, 0f, 0f);

        accumulated.Combine(Vector3.Zero, tiny);

        AssertQuaternionEquivalent(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI),
            accumulated.Orientation);
    }

    [Fact]
    public void Combine_OriginScale_DoesNotScaleRotation()
    {
        var accumulated = new MotionDeltaFrame();
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f);

        accumulated.Combine(new Vector3(0f, 0.5f, 0f), turn, originScale: 2f);

        AssertVectorEqual(new Vector3(0f, 1f, 0f), accumulated.Origin);
        AssertQuaternionEquivalent(turn, accumulated.Orientation);
    }

    [Fact]
    public void Reset_RestoresIdentityFrame()
    {
        var frame = new MotionDeltaFrame
        {
            Origin = Vector3.One,
            Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f),
        };

        frame.Reset();

        Assert.Equal(Vector3.Zero, frame.Origin);
        Assert.Equal(Quaternion.Identity, frame.Orientation);
    }

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }

    private static void AssertQuaternionEquivalent(Quaternion expected, Quaternion actual)
    {
        float alignment = MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(expected),
            Quaternion.Normalize(actual)));
        Assert.InRange(alignment, 0.99999f, 1.00001f);
    }

    public static TheoryData<Quaternion> InvalidRotations => new()
    {
        Quaternion.Zero,
        new Quaternion(float.NaN, 0f, 0f, 1f),
        new Quaternion(float.PositiveInfinity, 0f, 0f, 1f),
    };
}
