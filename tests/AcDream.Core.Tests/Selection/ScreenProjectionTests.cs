using System.Numerics;
using AcDream.Core.Selection;

namespace AcDream.Core.Tests.Selection;

public sealed class ScreenProjectionTests
{
    private static (Matrix4x4 view, Matrix4x4 proj, Vector2 viewport) StdCam()
    {
        var view     = Matrix4x4.Identity;
        var proj     = Matrix4x4.CreatePerspectiveFieldOfView(
                          MathF.PI * 0.5f /*fovY 90°*/, 800f / 600f, 0.1f, 100f);
        var viewport = new Vector2(800, 600);
        return (view, proj, viewport);
    }

    [Fact]
    public void TryProject_SphereInFront_ReturnsSquareRect()
    {
        var (view, proj, viewport) = StdCam();
        bool ok = ScreenProjection.TryProjectSphereToScreenRect(
            new Vector3(0, 0, -10), worldRadius: 1f,
            view, proj, viewport,
            out var rMin, out var rMax, out var depth,
            minSidePixels: 0f);

        Assert.True(ok);
        Assert.Equal(rMax.X - rMin.X, rMax.Y - rMin.Y, precision: 3);
        Assert.InRange((rMin.X + rMax.X) * 0.5f, 399f, 401f);
        Assert.InRange((rMin.Y + rMax.Y) * 0.5f, 299f, 301f);
        Assert.True(depth > 0f);
    }

    [Fact]
    public void TryProject_SphereBehindCamera_ReturnsFalse()
    {
        var (view, proj, viewport) = StdCam();
        bool ok = ScreenProjection.TryProjectSphereToScreenRect(
            new Vector3(0, 0, +10),
            worldRadius: 1f,
            view, proj, viewport,
            out _, out _, out _,
            minSidePixels: 0f);
        Assert.False(ok);
    }

    [Fact]
    public void TryProject_FarSphereClampsToMinSide()
    {
        var (view, proj, viewport) = StdCam();
        bool ok = ScreenProjection.TryProjectSphereToScreenRect(
            new Vector3(0, 0, -90) /* very far */, worldRadius: 0.01f /* tiny */,
            view, proj, viewport,
            out var rMin, out var rMax, out _,
            minSidePixels: 12f);
        Assert.True(ok);
        Assert.True(rMax.X - rMin.X >= 12f);
        Assert.True(rMax.Y - rMin.Y >= 12f);
    }
}
