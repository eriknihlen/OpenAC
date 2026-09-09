using System;
using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class ClipPlaneSetTests
{
    private static CellView RegularNgonCellView(int n, float radius)
    {
        var verts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            // CCW: increasing angle.
            float a = MathF.Tau * i / n;
            verts[i] = new Vector2(radius * MathF.Cos(a), radius * MathF.Sin(a));
        }
        var cv = new CellView();
        cv.Add(new ViewPolygon(verts));
        return cv;
    }

    private static CellView SquareCellView(float min, float max)
    {
        var cv = new CellView();
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(min, min), new Vector2(max, min), new Vector2(max, max), new Vector2(min, max),
        }));
        return cv;
    }

    [Fact]
    public void From_ViewPolygon_MatchesSinglePolygonCellView()
    {
        var polygon = new ViewPolygon(new[]
        {
            new Vector2(-0.7f, -0.4f), new Vector2(0.2f, -0.4f),
            new Vector2(0.7f, 0.3f), new Vector2(-0.5f, 0.6f),
        });
        var view = new CellView();
        Assert.True(view.Add(polygon));

        var fromView = ClipPlaneSet.From(view);
        var fromPolygon = ClipPlaneSet.From(polygon);

        Assert.Equal(fromView.Count, fromPolygon.Count);
        Assert.Equal(fromView.IsPlaneOverflow, fromPolygon.IsPlaneOverflow);
        Assert.Equal(fromView.IsNothingVisible, fromPolygon.IsNothingVisible);
        Assert.Equal(fromView.Planes, fromPolygon.Planes);
    }


    [Fact]
    public void From_AxisAlignedSquare_FourPlanes_PointInsideHasPositiveDistances()
    {
        var sq = new CellView();
        sq.Add(new ViewPolygon(new[]
        {
            new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f),
        }));
        var cps = ClipPlaneSet.From(sq);
        Assert.Equal(4, cps.Count);

        var clip = new Vector4(0, 0, 0, 1);            // NDC (0,0), inside
        foreach (var p in cps.Planes)
            Assert.True(Vector4.Dot(p, clip) >= 0);

        var outClip = new Vector4(0.9f, 0, 0, 1);      // NDC (0.9,0), outside the square
        Assert.Contains(cps.Planes, p => Vector4.Dot(p, outClip) < 0);
    }

    [Fact]
    public void From_NineEdgePolygon_FallsBackToPlaneOverflow()
    {
        var poly = RegularNgonCellView(n: 9, radius: 0.6f);
        var cps = ClipPlaneSet.From(poly);
        Assert.True(cps.IsPlaneOverflow || cps.Count <= 8);
        if (cps.IsPlaneOverflow)
        {
            Assert.Equal(0, cps.Count); // draws unclipped, no plane gate
        }
    }

    [Fact]
    public void From_EmptyRegion_IsEmpty()
    {
        Assert.Equal(0, ClipPlaneSet.From(new CellView()).Count);
    }

    // --- Multi-polygon safety (the under-inclusion guard) ---------------------

    [Fact]
    public void From_MultiplePolygons_FallsBackToPlaneOverflow_NeverEmitsOnePolygonsPlanes()
    {
        var cv = new CellView();
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(-0.8f, -0.8f), new Vector2(-0.4f, -0.8f), new Vector2(-0.4f, -0.4f), new Vector2(-0.8f, -0.4f),
        }));
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(0.4f, 0.4f), new Vector2(0.8f, 0.4f), new Vector2(0.8f, 0.8f), new Vector2(0.4f, 0.8f),
        }));

        var cps = ClipPlaneSet.From(cv);

        Assert.True(cps.IsPlaneOverflow);
        Assert.Equal(0, cps.Count);
        Assert.Empty(cps.Planes);
    }


    [Fact]
    public void Empty_IsNothingVisible_NotPlaneOverflow()
    {
        var e = ClipPlaneSet.From(new CellView());
        Assert.Equal(0, e.Count);
        Assert.False(e.IsPlaneOverflow);       // NOT "draws unclipped"
        Assert.True(e.IsNothingVisible);       // "draw nothing"
        Assert.Empty(e.Planes);
    }

    [Fact]
    public void Empty_StaticProperty_DrawsNothing()
    {
        var e = ClipPlaneSet.Empty;
        Assert.Equal(0, e.Count);
        Assert.False(e.IsPlaneOverflow);
        Assert.True(e.IsNothingVisible);
    }

    [Fact]
    public void PlaneOverflow_IsNotNothingVisible()
    {
        var poly = RegularNgonCellView(n: 9, radius: 0.6f);
        var cps = ClipPlaneSet.From(poly);
        Assert.True(cps.IsPlaneOverflow);
        Assert.False(cps.IsNothingVisible); // "draws unclipped", not "draw nothing"
    }


    [Fact]
    public void From_Square_EveryEdgePlane_IsPositiveAtCenter_NegativeJustOutsideThatEdge()
    {
        var cps = ClipPlaneSet.From(SquareCellView(-0.5f, 0.5f));
        Assert.Equal(4, cps.Count);

        var center = new Vector4(0, 0, 0, 1);
        // Inside ⇒ EVERY plane non-negative.
        foreach (var p in cps.Planes)
            Assert.True(Vector4.Dot(p, center) >= 0, $"plane {p} should be >=0 at center");

        foreach (var pt in new[]
        {
            new Vector4(0.6f, 0, 0, 1), new Vector4(-0.6f, 0, 0, 1),
            new Vector4(0, 0.6f, 0, 1), new Vector4(0, -0.6f, 0, 1),
        })
        {
            Assert.Contains(cps.Planes, p => Vector4.Dot(p, pt) < 0);
        }
    }

    [Fact]
    public void From_Triangle_ThreePlanes_AllZeroZComponent()
    {
        var cv = new CellView();
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(0f, 0.5f), new Vector2(-0.5f, -0.5f), new Vector2(0.5f, -0.5f), // CCW
        }));
        var cps = ClipPlaneSet.From(cv);
        Assert.Equal(3, cps.Count);
        foreach (var p in cps.Planes)
        {
            Assert.Equal(0f, p.Z, 6);
            // Normal is unit length in xy.
            Assert.Equal(1f, MathF.Sqrt(p.X * p.X + p.Y * p.Y), 4);
        }
    }

    // --- Winding normalization: a CW square must still yield inside-positive planes.

    [Fact]
    public void From_ClockwiseSquare_NormalizesWinding_InsideStillPositive()
    {
        var cv = new CellView();
        // Same square but wound CW (reverse order). Builder normally EnsureCcw's,
        // but From must be robust to either winding.
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(-0.5f, -0.5f), new Vector2(-0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, -0.5f),
        }));
        var cps = ClipPlaneSet.From(cv);
        Assert.Equal(4, cps.Count);
        var center = new Vector4(0, 0, 0, 1);
        foreach (var p in cps.Planes)
            Assert.True(Vector4.Dot(p, center) >= 0, $"plane {p} must be >=0 at center even for CW input");
    }


    [Fact]
    public void From_SquareWithCollinearMidpoint_MergesToFourPlanes()
    {
        var cv = new CellView();
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(-0.5f, -0.5f),
            new Vector2(0f, -0.5f),
            new Vector2(0.5f, -0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(-0.5f, 0.5f),
        }));
        var cps = ClipPlaneSet.From(cv);
        Assert.Equal(4, cps.Count);
    }

    // --- Exactly 8 edges fit (octagon); 9 spills to plane overflow (after merge fails to help).

    [Fact]
    public void From_RegularOctagon_FitsInEightPlanes()
    {
        var cps = ClipPlaneSet.From(RegularNgonCellView(n: 8, radius: 0.6f));
        Assert.False(cps.IsPlaneOverflow);
        Assert.Equal(8, cps.Count);
        var center = new Vector4(0, 0, 0, 1);
        foreach (var p in cps.Planes)
            Assert.True(Vector4.Dot(p, center) >= 0);
    }


    [Fact]
    public void From_DegenerateSinglePolygon_IsNothingVisible()
    {
        var cv = new CellView();
        cv.Add(new ViewPolygon(new[]
        {
            new Vector2(-0.5f, -0.5f), new Vector2(0f, 0f), new Vector2(0.5f, 0.5f),
        }));
        var cps = ClipPlaneSet.From(cv);
        Assert.Equal(0, cps.Count);
        Assert.True(cps.IsNothingVisible);
    }

}
