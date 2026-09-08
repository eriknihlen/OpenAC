using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Tests.Physics;

public sealed class PositionFrameValidationTests
{
    [Fact]
    public void ValidPositionRequiresInboundCellAndRetailFrameValidity()
    {
        Assert.True(PositionFrameValidation.IsValid(
            0x0101FFFFu, new Vector3(1f, 2f, 3f), Quaternion.Identity));
        Assert.False(PositionFrameValidation.IsValid(
            0u, new Vector3(1f, 2f, 3f), Quaternion.Identity));
        Assert.False(PositionFrameValidation.IsValid(
            0x0101FFFFu, new Vector3(float.NaN, 2f, 3f), Quaternion.Identity));
        Assert.False(PositionFrameValidation.IsValid(
            0x0101FFFFu, new Vector3(1f, 2f, 3f), new Quaternion(0f, 0f, 0f, 0.9f)));
    }

    [Theory]
    [InlineData(1.001f, true)]
    [InlineData(1.0011f, false)]
    public void QuaternionSquaredNormUsesRetailTolerance(float squaredNorm, bool expected)
    {
        var rotation = new Quaternion(0f, 0f, 0f, MathF.Sqrt(squaredNorm));

        Assert.Equal(expected, PositionFrameValidation.IsValid(
            0x0101FFFFu, Vector3.Zero, rotation));
    }
}
