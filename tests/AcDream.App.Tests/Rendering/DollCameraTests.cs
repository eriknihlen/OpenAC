using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class DollCameraTests
{
    [Fact]
    public void Eye_position_is_retail_verbatim()
    {
        var cam = new DollCamera { Aspect = 100f / 214f };
        Assert.True(Matrix4x4.Invert(cam.View, out var inv));
        var eye = inv.Translation;
        Assert.Equal(0.12f, eye.X, 3);
        Assert.Equal(-2.4f, eye.Y, 3);
        Assert.Equal(0.88f, eye.Z, 3);
    }

    [Fact]
    public void Look_axis_is_pure_plus_y_zero_yaw()
    {
        var cam = new DollCamera { Aspect = 100f / 214f };
        var forward = -new Vector3(cam.View.M13, cam.View.M23, cam.View.M33);
        Assert.Equal(0f, forward.X, 4);
        Assert.Equal(1f, forward.Y, 4);
        Assert.Equal(0f, forward.Z, 4);
    }

    [Fact]
    public void Projection_is_finite_and_uses_aspect()
    {
        var cam = new DollCamera { Aspect = 1.5f };
        Assert.True(float.IsFinite(cam.Projection.M11));
        Assert.NotEqual(0f, cam.Projection.M34);   // perspective w = -z term
    }
}
