using System.Numerics;
using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

public sealed class WalkVisibilityMathTests
{

    [Fact]
    public void Up_normal_plane_encodes_inside_above_as_negative_height()
    {
        var plane = new WalkPlane(new Vector3(0, 0, 1), -5f);
        Assert.Equal(-5f, WalkVisibilityMath.GetPointLimit(0, 0, plane));
    }

    [Fact]
    public void Up_normal_plane_at_or_above_sky_height_is_outside()
    {
        var plane = new WalkPlane(new Vector3(0, 0, 1), -1000f);
        Assert.Equal(
            WalkVisibilityMath.OutsideColumn,
            WalkVisibilityMath.GetPointLimit(0, 0, plane));
    }

    [Fact]
    public void Up_normal_plane_with_nonpositive_height_is_wholly_inside()
    {
        var plane = new WalkPlane(new Vector3(0, 0, 1), 3f);   // z >= -3
        Assert.Equal(
            WalkVisibilityMath.InsideColumn,
            WalkVisibilityMath.GetPointLimit(0, 0, plane));
    }

    [Fact]
    public void Down_normal_plane_encodes_inside_below_as_positive_height()
    {
        // Plane z <= 7: N=(0,0,-1), d=7 → h=7, inside below.
        var plane = new WalkPlane(new Vector3(0, 0, -1), 7f);
        Assert.Equal(7f, WalkVisibilityMath.GetPointLimit(0, 0, plane));
    }

    [Fact]
    public void Down_normal_plane_with_nonpositive_height_is_outside()
    {
        var plane = new WalkPlane(new Vector3(0, 0, -1), -2f);  // z <= -2: nothing above ground
        Assert.Equal(
            WalkVisibilityMath.OutsideColumn,
            WalkVisibilityMath.GetPointLimit(0, 0, plane));
    }

    [Fact]
    public void Vertical_plane_uses_the_side_of_the_ground_point()
    {
        var plane = new WalkPlane(new Vector3(1, 0, 0), -10f);  // x >= 10
        Assert.Equal(
            WalkVisibilityMath.OutsideColumn,
            WalkVisibilityMath.GetPointLimit(5f, 0, plane));
        Assert.Equal(
            WalkVisibilityMath.InsideColumn,
            WalkVisibilityMath.GetPointLimit(15f, 0, plane));
        Assert.Equal(
            WalkVisibilityMath.InsideColumn,
            WalkVisibilityMath.GetPointLimit(10f, 0, plane));
    }


    [Theory]
    [InlineData(1001f, 0f, 10f, WalkBoundingType.Outside)]         // sentinel outside
    [InlineData(0f, 0f, 10f, WalkBoundingType.EntirelyInside)]     // sentinel inside
    [InlineData(-5f, 6f, 10f, WalkBoundingType.EntirelyInside)]    // inside above 5; slab [6,10] wholly above
    [InlineData(-5f, 2f, 10f, WalkBoundingType.PartiallyInside)]   // slab straddles 5
    [InlineData(-5f, 2f, 4f, WalkBoundingType.Outside)]            // slab wholly below 5
    [InlineData(7f, 2f, 6f, WalkBoundingType.EntirelyInside)]      // inside below 7; slab wholly below
    [InlineData(7f, 2f, 10f, WalkBoundingType.PartiallyInside)]    // slab straddles 7
    [InlineData(7f, 8f, 10f, WalkBoundingType.Outside)]            // slab wholly above 7
    public void Corner_check_classifies_the_slab(
        float bound, float minZ, float maxZ, WalkBoundingType expected)
        => Assert.Equal(expected, WalkVisibilityMath.CornerPlaneCheck(bound, minZ, maxZ));

    [Fact]
    public void Corner_check_boundary_touch_is_out_on_the_out_side_and_in_on_the_in_side()
    {
        Assert.Equal(
            WalkBoundingType.Outside,
            WalkVisibilityMath.CornerPlaneCheck(-5f, 2f, 5f));
        Assert.Equal(
            WalkBoundingType.EntirelyInside,
            WalkVisibilityMath.CornerPlaneCheck(-5f, 5f, 10f));
        // Inside-below: minZ == h rejects; maxZ == h accepts entirely.
        Assert.Equal(
            WalkBoundingType.Outside,
            WalkVisibilityMath.CornerPlaneCheck(5f, 5f, 10f));
        Assert.Equal(
            WalkBoundingType.EntirelyInside,
            WalkVisibilityMath.CornerPlaneCheck(5f, 2f, 5f));
    }


    [Fact]
    public void Plane_check_requires_unanimity_for_outside_and_entirely_inside()
    {
        Assert.Equal(
            WalkBoundingType.Outside,
            WalkVisibilityMath.BlockPlaneCheck(1001f, 1001f, 1001f, 1001f, 0f, 10f));
        Assert.Equal(
            WalkBoundingType.EntirelyInside,
            WalkVisibilityMath.BlockPlaneCheck(0f, 0f, 0f, 0f, 0f, 10f));
        // 3-of-4 outside is still PARTIAL (the block may straddle the plane).
        Assert.Equal(
            WalkBoundingType.PartiallyInside,
            WalkVisibilityMath.BlockPlaneCheck(1001f, 1001f, 1001f, 0f, 0f, 10f));
        Assert.Equal(
            WalkBoundingType.PartiallyInside,
            WalkVisibilityMath.BlockPlaneCheck(0f, 0f, 0f, 1001f, 0f, 10f));
    }


    [Fact]
    public void Block_check_culls_on_any_single_fully_outside_plane()
    {
        float[] c = [0f, 1001f];
        Assert.Equal(
            WalkBoundingType.Outside,
            WalkVisibilityMath.BlockCheck(c, c, c, c, planeCount: 1, maxZ: 10f, minZ: 0f));
    }

    [Fact]
    public void Block_check_demotion_is_sticky_across_planes()
    {
        float[] cornerA = [0f, -5f, 0f];   // straddles h=5 for slab [2,10]
        float[] cornerB = [0f, 0f, 0f];
        Assert.Equal(
            WalkBoundingType.PartiallyInside,
            WalkVisibilityMath.BlockCheck(
                cornerA, cornerB, cornerB, cornerB, planeCount: 2, maxZ: 10f, minZ: 2f));
    }

    [Fact]
    public void Block_check_is_entirely_inside_only_with_unanimity_on_every_plane()
    {
        float[] c = [0f, 0f, 0f];
        Assert.Equal(
            WalkBoundingType.EntirelyInside,
            WalkVisibilityMath.BlockCheck(c, c, c, c, planeCount: 2, maxZ: 10f, minZ: 0f));
    }


    [Fact]
    public void Clip_heights_write_the_cy_plane_then_every_edge_plane()
    {
        var cy = new WalkPlane(new Vector3(0, 0, 1), -5f);
        WalkPlane[] edges =
        [
            new(new Vector3(0, 0, -1), 20f),
            new(new Vector3(1, 0, 0), -100f),
        ];
        Span<float> bounds = stackalloc float[3];

        WalkVisibilityMath.FillClipHeights(0f, 0f, cy, edges, bounds);

        Assert.Equal(-5f, bounds[0]);
        Assert.Equal(20f, bounds[1]);
        Assert.Equal(WalkVisibilityMath.OutsideColumn, bounds[2]);
    }


    private static readonly WalkPlane Cy = new(new Vector3(0, 1, 0), 0f); // forward = +Y, eye at origin

    [Fact]
    public void Sphere_fully_behind_the_near_plane_is_outside()
        => Assert.Equal(
            WalkBoundingType.Outside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(0, -5, 0), 1f, Cy, []));

    [Fact]
    public void Cull_is_strict_and_partial_is_inclusive_at_the_boundary()
    {
        Assert.Equal(
            WalkBoundingType.PartiallyInside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(0, -1, 0), 1f, Cy, []));
        Assert.Equal(
            WalkBoundingType.PartiallyInside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(0, 1, 0), 1f, Cy, []));
        Assert.Equal(
            WalkBoundingType.EntirelyInside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(0, 1.01f, 0), 1f, Cy, []));
    }

    [Fact]
    public void Edge_planes_cull_and_demote_like_the_cy_plane()
    {
        WalkPlane[] edges = [new(new Vector3(1, 0, 0), 0f)];   // inside is x >= 0
        Assert.Equal(
            WalkBoundingType.Outside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(-3, 5, 0), 1f, Cy, edges));
        Assert.Equal(
            WalkBoundingType.PartiallyInside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(0.5f, 5, 0), 1f, Cy, edges));
        Assert.Equal(
            WalkBoundingType.EntirelyInside,
            WalkVisibilityMath.ViewconeCheck(new Vector3(3, 5, 0), 1f, Cy, edges));
    }


    [Fact]
    public void Boundary_guard_rejects_a_polygon_whose_every_vertex_sits_on_the_same_plusX_plane()
    {
        Vector3[] polygon = [new Vector3(12f, -3f, 3f), new Vector3(12f, 0f, 3f), new Vector3(12f, 5f, 3f)];
        Assert.True(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_rejects_a_polygon_whose_every_vertex_sits_on_the_same_minusY_plane()
    {
        Vector3[] polygon = [new Vector3(-3f, -12f, 3f), new Vector3(0f, -12f, 3f), new Vector3(5f, -12f, 3f)];
        Assert.True(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_admits_a_polygon_with_only_one_vertex_on_plusX12_the_rest_inside()
    {
        Vector3[] polygon = [new Vector3(0f, 0f, 3f), new Vector3(12f, 0f, 3f), new Vector3(5f, 5f, 3f)];
        Assert.False(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_admits_a_polygon_whose_every_vertex_is_just_inside_11_999()
    {
        Vector3[] polygon = [new Vector3(11.999f, 0f, 3f), new Vector3(11.999f, 5f, 3f), new Vector3(11.999f, -5f, 3f)];
        Assert.False(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_admits_a_polygon_split_across_plusX12_and_plusY12_no_common_plane()
    {
        Vector3[] polygon = [new Vector3(12f, 0f, 3f), new Vector3(0f, 12f, 3f), new Vector3(-5f, -5f, 3f)];
        Assert.False(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_admits_a_polygon_whose_every_vertex_is_on_SOME_plane_but_not_the_SAME_one()
    {
        Vector3[] polygon = [new Vector3(12f, 0f, 3f), new Vector3(0f, 12f, 3f), new Vector3(12f, 5f, 3f)];
        Assert.False(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_ignores_the_vertical_z_component()
    {
        Vector3[] polygon = [new Vector3(0, 0, 12f), new Vector3(1, 1, -12f), new Vector3(2, 0, 0)];
        Assert.False(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard(polygon));
    }

    [Fact]
    public void Boundary_guard_rejects_the_empty_polygon_because_every_plane_predicate_survives_vacuously()
    {
        Assert.True(WalkVisibilityMath.IsRejectedByPortalPolygonBoundaryGuard([]));
    }
}
