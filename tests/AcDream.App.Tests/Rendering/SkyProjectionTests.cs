using System.Numerics;
using AcDream.App.Rendering.Sky;

namespace AcDream.App.Tests.Rendering;

public sealed class SkyProjectionTests
{
    [Theory]
    [InlineData(60f)]
    [InlineData(120f)]
    [InlineData(179.885f)]
    public void WithDepthRange_PreservesActiveFieldOfView(float degrees)
    {
        Matrix4x4 active = Matrix4x4.CreatePerspectiveFieldOfView(
            degrees * MathF.PI / 180f,
            16f / 9f,
            0.1f,
            5_000f);

        Matrix4x4 sky = SkyProjection.WithDepthRange(active, 0.1f, 1_000_000f);

        Assert.Equal(active.M11, sky.M11);
        Assert.Equal(active.M22, sky.M22);
        Assert.Equal(active.M34, sky.M34);
        Assert.Equal(active.M44, sky.M44);
        Assert.Equal(1_000_000f / (0.1f - 1_000_000f), sky.M33, precision: 6);
        Assert.Equal(0.1f * 1_000_000f / (0.1f - 1_000_000f), sky.M43, precision: 6);
    }

    [Fact]
    public void WithDepthRange_PreservesOffCenterProjectionTerms()
    {
        Matrix4x4 active = Matrix4x4.CreatePerspectiveOffCenter(
            -0.8f, 1.2f, -0.5f, 0.7f, 0.1f, 5_000f);

        Matrix4x4 sky = SkyProjection.WithDepthRange(active, 1f, 10_000f);

        Assert.Equal(active.M11, sky.M11);
        Assert.Equal(active.M22, sky.M22);
        Assert.Equal(active.M31, sky.M31);
        Assert.Equal(active.M32, sky.M32);
    }

    [Fact]
    public void WithDepthRange_RejectsNonPerspectiveProjection()
    {
        Matrix4x4 orthographic = Matrix4x4.CreateOrthographic(10f, 10f, 0.1f, 100f);

        Assert.Throws<ArgumentException>(() =>
            SkyProjection.WithDepthRange(orthographic, 0.1f, 1_000f));
    }
}
