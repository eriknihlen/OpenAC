using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class ScreenPolygonClipTests
{
    // CCW square [min,max]^2.
    private static Vector2[] Square(float min, float max) => new[]
    {
        new Vector2(min, min), new Vector2(max, min), new Vector2(max, max), new Vector2(min, max),
    };

    private static float Area(Vector2[] poly)
    {
        if (poly.Length < 3) return 0f;
        float a = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Length];
            a += p.X * q.Y - q.X * p.Y;
        }
        return System.MathF.Abs(a) * 0.5f;
    }

    [Fact]
    public void Intersect_FullyContained_ReturnsSubject()
    {
        var outer = Square(-1f, 1f);
        var inner = Square(-0.5f, 0.5f);
        var r = ScreenPolygonClip.Intersect(inner, outer);
        Assert.Equal(1.0f, Area(r), 3); // inner area = 1x1
    }

    [Fact]
    public void Intersect_PartialOverlap_ReturnsOverlapArea()
    {
        var a = Square(0f, 2f);
        var b = Square(1f, 3f);
        var r = ScreenPolygonClip.Intersect(a, b);
        Assert.Equal(1.0f, Area(r), 3); // overlap [1,2]^2 = 1
    }

    [Fact]
    public void Intersect_Disjoint_ReturnsEmpty()
    {
        var a = Square(0f, 1f);
        var b = Square(5f, 6f);
        var r = ScreenPolygonClip.Intersect(a, b);
        Assert.True(r.Length < 3); // empty
    }

    [Fact]
    public void Intersect_Sliver_PreservesNarrowOverlap()
    {
        var window = Square(-1f, 1f);
        var stairwell = new[]
        {
            new Vector2(-0.1f, -1f), new Vector2(0.1f, -1f), new Vector2(0.1f, 1f), new Vector2(-0.1f, 1f),
        };
        var r = ScreenPolygonClip.Intersect(window, stairwell);
        Assert.Equal(0.4f, Area(r), 3); // 0.2 wide x 2 tall
        Assert.True(System.MathF.Abs(r[0].X) <= 0.1001f);
    }
}
