using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkCopyViewTests
{
    private sealed class LinearRayCaster : IWalkRayCaster
    {
        public Vector3 RayThrough(float screenX, float screenY)
            => new(screenX, screenY, 100f);
    }

    private static readonly LinearRayCaster Rays = new();
    private static readonly Vector3 Eye = new(1f, 2f, 3f);

    private static WalkScreenPoint Pt(float x, float y, float w = 1f)
        => new(x * w, y * w, 0f, w);

    [Fact]
    public void Full_viewport_quad_appends_retails_root_view()
    {
        var dest = new WalkPortalView();

        bool ok = WalkCopyView.AppendFullViewportQuad(dest, Rays, Eye, 640f, 480f);

        Assert.True(ok);
        Assert.Equal(1, dest.ViewCount);
        WalkViewPoly poly = dest.View.Polys[0];
        Assert.Equal(4, poly.VertexCount);
        Assert.Equal(0, poly.VertexIndex);
        Assert.Equal((0f, 640f, 0f, 480f), (poly.XMin, poly.XMax, poly.YMin, poly.YMax));
        Assert.Equal(new Vector2(0, 480), dest.View.Vertices[0].Point);
        Assert.Equal(new Vector2(640, 480), dest.View.Vertices[1].Point);
        Assert.Equal(new Vector2(640, 0), dest.View.Vertices[2].Point);
        Assert.Equal(new Vector2(0, 0), dest.View.Vertices[3].Point);
        Assert.Equal(dest.View.Vertices[0].Point, dest.View.Vertices[4].Point);
        Assert.Equal(5, dest.View.VertexCountTotal);
    }

    [Fact]
    public void Collinear_midpoint_on_an_edge_is_pruned()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> square =
        [
            Pt(0, 0), Pt(50, 0), Pt(100, 0), Pt(100, 100), Pt(0, 100),
        ];

        bool ok = WalkCopyView.Append(dest, square, Rays, Eye);

        Assert.True(ok);
        Assert.Equal(4, dest.View.Polys[0].VertexCount);
        Assert.Equal(new Vector2(0, 0), dest.View.Vertices[0].Point);
        Assert.Equal(new Vector2(100, 0), dest.View.Vertices[1].Point);
        Assert.Equal(new Vector2(100, 100), dest.View.Vertices[2].Point);
        Assert.Equal(new Vector2(0, 100), dest.View.Vertices[3].Point);
    }

    [Fact]
    public void Near_duplicate_points_within_one_pixel_are_dropped()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> poly =
        [
            Pt(0, 0), Pt(0.5f, 0.5f), Pt(100, 0), Pt(50, 100),
        ];

        bool ok = WalkCopyView.Append(dest, poly, Rays, Eye);

        Assert.True(ok);
        Assert.Equal(3, dest.View.Polys[0].VertexCount);
    }

    [Fact]
    public void Fewer_than_three_survivors_reject_and_leave_dest_untouched()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> tiny =
        [
            Pt(0, 0), Pt(0.5f, 0f), Pt(0f, 0.5f),
        ];

        bool ok = WalkCopyView.Append(dest, tiny, Rays, Eye);

        Assert.False(ok);
        Assert.Equal(0, dest.ViewCount);
        Assert.Empty(dest.View.Polys);
        Assert.Empty(dest.View.Vertices);
        Assert.Equal(0, dest.View.VertexCountTotal);
    }

    [Fact]
    public void Homogeneous_points_are_perspective_divided_before_storage()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> tri =
        [
            Pt(0, 0, w: 2f), Pt(100, 0, w: 2f), Pt(50, 100, w: 2f),
        ];

        bool ok = WalkCopyView.Append(dest, tri, Rays, Eye);

        Assert.True(ok);
        Assert.Equal(new Vector2(0, 0), dest.View.Vertices[0].Point);
        Assert.Equal(new Vector2(100, 0), dest.View.Vertices[1].Point);
        Assert.Equal(new Vector2(50, 100), dest.View.Vertices[2].Point);
    }

    [Fact]
    public void Edge_planes_are_next_cross_current_normalized_through_the_eye()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> tri = [Pt(0, 0), Pt(100, 0), Pt(50, 100)];

        Assert.True(WalkCopyView.Append(dest, tri, Rays, Eye));

        Vector3 ray0 = Rays.RayThrough(0, 0);
        Vector3 ray1 = Rays.RayThrough(100, 0);
        Vector3 expected = Vector3.Normalize(Vector3.Cross(ray1, ray0));
        WalkPlane plane = dest.View.Vertices[0].Plane;
        Assert.Equal(expected.X, plane.Normal.X, 5);
        Assert.Equal(expected.Y, plane.Normal.Y, 5);
        Assert.Equal(expected.Z, plane.Normal.Z, 5);
        Assert.Equal(-Vector3.Dot(expected, Eye), plane.D, 3);
    }

    [Fact]
    public void Pool_resets_when_view_count_returns_to_zero()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> tri = [Pt(0, 0), Pt(100, 0), Pt(50, 100)];
        Assert.True(WalkCopyView.Append(dest, tri, Rays, Eye));
        int firstTotal = dest.View.VertexCountTotal;

        dest.ResetForPush();
        Span<WalkScreenPoint> tri2 = [Pt(0, 0), Pt(200, 0), Pt(100, 200)];
        Assert.True(WalkCopyView.Append(dest, tri2, Rays, Eye));

        Assert.Equal(1, dest.ViewCount);
        Assert.Single(dest.View.Polys);
        Assert.Equal(firstTotal, dest.View.VertexCountTotal);   // pool restarted at 0
        Assert.Equal(0, dest.View.Polys[0].VertexIndex);
    }

    [Fact]
    public void Second_append_extends_the_shared_pool()
    {
        var dest = new WalkPortalView();
        Span<WalkScreenPoint> tri = [Pt(0, 0), Pt(100, 0), Pt(50, 100)];
        Assert.True(WalkCopyView.Append(dest, tri, Rays, Eye));
        Span<WalkScreenPoint> tri2 = [Pt(0, 0), Pt(200, 0), Pt(100, 200)];

        Assert.True(WalkCopyView.Append(dest, tri2, Rays, Eye));

        Assert.Equal(2, dest.ViewCount);
        Assert.Equal(2, dest.View.Polys.Count);
        Assert.Equal(4, dest.View.Polys[1].VertexIndex);   // after tri's 3 + dup
        Assert.Equal(8, dest.View.VertexCountTotal);
    }
}
