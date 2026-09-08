using System.Numerics;
using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class VividTargetIndicatorLayoutTests
{
    private static readonly (float Width, float Height)[] RetailCorners =
        [(12f, 12f), (12f, 12f), (12f, 12f), (12f, 12f)];

    [Fact]
    public void CornersSitOutsideSelectionSphereRectangle()
    {
        VividTargetLayout layout = VividTargetIndicatorController.ComputeLayout(
            new Vector2(100f, 80f),
            new Vector2(180f, 200f),
            new Vector2(800f, 600f),
            RetailCorners);

        Assert.Equal(new Vector2(88f, 68f), layout.RootPosition);
        Assert.Equal(new Vector2(104f, 144f), layout.RootSize);
        Assert.Equal(Vector2.Zero, layout.CornerPositions[0]);
        Assert.Equal(new Vector2(92f, 0f), layout.CornerPositions[1]);
        Assert.Equal(new Vector2(92f, 132f), layout.CornerPositions[2]);
        Assert.Equal(new Vector2(0f, 132f), layout.CornerPositions[3]);
    }

    [Fact]
    public void WholeIndicatorClampsToRetailEightPixelViewportMargin()
    {
        VividTargetLayout layout = VividTargetIndicatorController.ComputeLayout(
            new Vector2(2f, 3f),
            new Vector2(62f, 83f),
            new Vector2(200f, 150f),
            RetailCorners);

        Assert.Equal(new Vector2(8f, 8f), layout.RootPosition);
        Assert.Equal(new Vector2(66f, 87f), layout.RootSize);
    }

    [Fact]
    public void ProjectionHasNoDistanceOrWorldDrawVisibilityGate()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 2f, 4f / 3f, 0.1f, 100f);

        VividTargetProjection result = VividTargetIndicatorController.ComputeProjection(
            new Vector3(0f, 0f, -1_000_000f),
            1f,
            Matrix4x4.Identity,
            projection,
            new Vector2(800f, 600f));

        Assert.Equal(VividTargetProjectionStatus.OnScreen, result.Status);
        Assert.InRange((result.RectMin.X + result.RectMax.X) * 0.5f, 399.9f, 400.1f);
        Assert.InRange((result.RectMin.Y + result.RectMax.Y) * 0.5f, 299.9f, 300.1f);
    }

    [Theory]
    [InlineData(100f, 0f, -10f, 0f, 6u)]
    [InlineData(0f, 100f, -10f, 90f, 9u)]
    [InlineData(-100f, 0f, -10f, 180f, 11u)]
    [InlineData(0f, -100f, -10f, 270f, 8u)]
    public void OffScreenDirectionMatchesRetailViewerAxes(
        float x, float y, float z, float expectedAngle, uint expectedImageEnum)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 2f, 4f / 3f, 0.1f, 100f);

        VividTargetProjection result = VividTargetIndicatorController.ComputeProjection(
            new Vector3(x, y, z),
            1f,
            Matrix4x4.Identity,
            projection,
            new Vector2(800f, 600f));

        Assert.Equal(VividTargetProjectionStatus.OffScreen, result.Status);
        Assert.Equal(expectedAngle, result.AngleDegrees, precision: 3);
        Assert.Equal(expectedImageEnum,
            VividTargetIndicatorController.SelectOffScreenImageEnum(result.AngleDegrees));
    }

    [Theory]
    [InlineData(0f, 768f, 288f)]
    [InlineData(90f, 388f, 8f)]
    [InlineData(180f, 8f, 288f)]
    [InlineData(270f, 388f, 568f)]
    public void OffScreenImageClampsToRetailEightPixelEdge(
        float angle, float expectedLeft, float expectedTop)
    {
        Vector2 position = VividTargetIndicatorController.ComputeOffScreenPosition(
            angle,
            new Vector2(800f, 600f),
            new Vector2(24f, 24f));

        Assert.Equal(expectedLeft, position.X, precision: 3);
        Assert.Equal(expectedTop, position.Y, precision: 3);
    }

    [Theory]
    [InlineData(0f, 6u)]
    [InlineData(22.999f, 6u)]
    [InlineData(23f, 7u)]
    [InlineData(68f, 9u)]
    [InlineData(113f, 12u)]
    [InlineData(158f, 11u)]
    [InlineData(203f, 10u)]
    [InlineData(248f, 8u)]
    [InlineData(293f, 5u)]
    [InlineData(338f, 6u)]
    public void DirectionImageUsesRetailSectorBoundaries(float angle, uint expected)
        => Assert.Equal(expected,
            VividTargetIndicatorController.SelectOffScreenImageEnum(angle));
}
