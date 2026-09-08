using System.Numerics;
using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public class WbFrustumTests
{
    private static Matrix4x4 PerspectiveVp(Vector3 eye, Vector3 lookAt) =>
        Matrix4x4.CreateLookAt(eye, lookAt, Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1.0f, 0.1f, 100.0f);

    [Fact]
    public void TestBox_BoxInFrontOfCamera_ReturnsInsideOrIntersecting()
    {
        var f = new WbFrustum();
        f.Update(PerspectiveVp(Vector3.Zero, new Vector3(0, 0, -1)));
        var box = new WbBoundingBox(new Vector3(-1, -1, -10), new Vector3(1, 1, -2));
        var res = f.TestBox(box);
        Assert.NotEqual(FrustumTestResult.Outside, res);
    }

    [Fact]
    public void TestBox_BoxBehindCamera_ReturnsOutside()
    {
        var f = new WbFrustum();
        f.Update(PerspectiveVp(Vector3.Zero, new Vector3(0, 0, -1)));
        var box = new WbBoundingBox(new Vector3(-1, -1, 2), new Vector3(1, 1, 10));
        Assert.Equal(FrustumTestResult.Outside, f.TestBox(box));
    }

    [Fact]
    public void Intersects_BoxInFrontOfCamera_ReturnsTrue()
    {
        var f = new WbFrustum();
        f.Update(PerspectiveVp(Vector3.Zero, new Vector3(0, 0, -1)));
        var box = new WbBoundingBox(new Vector3(-1, -1, -10), new Vector3(1, 1, -2));
        Assert.True(f.Intersects(box));
    }

    [Fact]
    public void Update_IsIdempotent()
    {
        var f = new WbFrustum();
        var vp = PerspectiveVp(Vector3.Zero, new Vector3(0, 0, -1));
        f.Update(vp);
        f.Update(vp);
        var box = new WbBoundingBox(new Vector3(-1, -1, -10), new Vector3(1, 1, -2));
        Assert.NotEqual(FrustumTestResult.Outside, f.TestBox(box));
    }

    [Fact]
    public void WbBoundingBox_Union_ExtendsToCoverBoth()
    {
        var a = new WbBoundingBox(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
        var b = new WbBoundingBox(new Vector3(2, 2, 2), new Vector3(3, 3, 3));
        var u = WbBoundingBox.Union(a, b);
        Assert.Equal(new Vector3(0, 0, 0), u.Min);
        Assert.Equal(new Vector3(3, 3, 3), u.Max);
    }

    [Fact]
    public void Intersects_BoxBehindCamera_ReturnsFalse()
    {
        var f = new WbFrustum();
        f.Update(PerspectiveVp(Vector3.Zero, new Vector3(0, 0, -1)));
        var box = new WbBoundingBox(new Vector3(-1, -1, 2), new Vector3(1, 1, 10));
        Assert.False(f.Intersects(box));
    }

    [Fact]
    public void TestBox_IgnoreNearPlane_DoesNotReturnOutsideForNearOverlap()
    {
        var f = new WbFrustum();
        f.Update(PerspectiveVp(Vector3.Zero, new Vector3(0, 0, -1)));
        var box = new WbBoundingBox(new Vector3(-0.5f, -0.5f, -5f), new Vector3(0.5f, 0.5f, -0.05f));
        var withNear = f.TestBox(box, ignoreNearPlane: false);
        var withoutNear = f.TestBox(box, ignoreNearPlane: true);
        Assert.NotEqual(FrustumTestResult.Outside, withoutNear);
        _ = withNear;
    }

    [Fact]
    public void WbBoundingBox_Union_WithOverlapping_CoversAll()
    {
        var a = new WbBoundingBox(new Vector3(-1, -1, -1), new Vector3(2, 2, 2));
        var b = new WbBoundingBox(new Vector3(0, 0, 0), new Vector3(3, 3, 3));
        var u = WbBoundingBox.Union(a, b);
        Assert.Equal(new Vector3(-1, -1, -1), u.Min);
        Assert.Equal(new Vector3(3, 3, 3), u.Max);
    }
}
