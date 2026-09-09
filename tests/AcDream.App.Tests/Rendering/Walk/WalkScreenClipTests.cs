using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkScreenClipTests
{
    private const float W = 640f, H = 480f;

    private static readonly Vector2[] RootQuad =
    [
        new(0, H), new(W, H), new(W, 0), new(0, 0),
    ];

    private static WalkScreenPoint Pt(float x, float y, float w = 1f)
        => new(x * w, y * w, 0f, w);   // homogeneous: screen * w

    [Fact]
    public void Transform_maps_clip_center_to_screen_center_with_y_flip()
    {
        Matrix4x4 identity = Matrix4x4.Identity;

        WalkScreenPoint center = WalkScreenClip.TransformToScreen(
            Vector3.Zero, identity, W, H);
        Assert.Equal(W / 2, center.X);
        Assert.Equal(H / 2, center.Y);
        Assert.Equal(1f, center.W);

        WalkScreenPoint top = WalkScreenClip.TransformToScreen(
            new Vector3(0, 1, 0), identity, W, H);
        Assert.Equal(0f, top.Y);
    }

    [Fact]
    public void Fully_inside_polygon_survives_unchanged_with_original_winding()
    {
        Span<WalkScreenPoint> tri = [Pt(100, 100), Pt(300, 120), Pt(200, 300)];
        Span<WalkScreenPoint> outPts = stackalloc WalkScreenPoint[16];

        int n = WalkScreenClip.ClipAgainstView(tri, RootQuad, outPts);

        Assert.Equal(3, n);
        Assert.Equal(100f, outPts[0].X);
        Assert.Equal(300f, outPts[1].X);
        Assert.Equal(200f, outPts[2].X);
    }

    [Fact]
    public void Polygon_straddling_the_left_edge_is_clipped_at_x_zero()
    {
        Span<WalkScreenPoint> tri = [Pt(-100, 100), Pt(100, 100), Pt(100, 300)];
        Span<WalkScreenPoint> outPts = stackalloc WalkScreenPoint[16];

        int n = WalkScreenClip.ClipAgainstView(tri, RootQuad, outPts);

        Assert.True(n >= 3);
        for (int i = 0; i < n; i++)
            Assert.True(outPts[i].X / outPts[i].W >= -0.001f, $"vertex {i} left of x=0");
        // Something was actually cut (an intersection vertex exists at x≈0).
        bool touchesEdge = false;
        for (int i = 0; i < n; i++)
            if (MathF.Abs(outPts[i].X / outPts[i].W) < 0.001f) touchesEdge = true;
        Assert.True(touchesEdge);
    }

    [Fact]
    public void Polygon_fully_outside_one_edge_returns_zero()
    {
        Span<WalkScreenPoint> tri = [Pt(-300, 100), Pt(-100, 100), Pt(-200, 300)];
        Span<WalkScreenPoint> outPts = stackalloc WalkScreenPoint[16];

        Assert.Equal(0, WalkScreenClip.ClipAgainstView(tri, RootQuad, outPts));
    }

    [Fact]
    public void W_plane_clips_points_behind_the_eye()
    {
        Span<WalkScreenPoint> tri =
        [
            Pt(100, 100), Pt(300, 100), new WalkScreenPoint(200, 200, 0, -0.5f),
        ];
        Span<WalkScreenPoint> outPts = stackalloc WalkScreenPoint[16];

        int n = WalkScreenClip.ClipAgainstView(tri, RootQuad, outPts);

        Assert.True(n >= 3);
        for (int i = 0; i < n; i++)
            Assert.True(outPts[i].W >= WalkScreenClip.MinW - 1e-6f);
    }
}
