using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class PortalViewTests
{
    [Fact]
    public void ViewPolygon_ComputesBoundingRect()
    {
        var p = new ViewPolygon(new[]
        {
            new Vector2(-0.5f, -0.25f), new Vector2(0.5f, -0.25f), new Vector2(0.0f, 0.75f),
        });
        Assert.Equal(-0.5f, p.MinX, 5);
        Assert.Equal(0.5f, p.MaxX, 5);
        Assert.Equal(-0.25f, p.MinY, 5);
        Assert.Equal(0.75f, p.MaxY, 5);
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void ViewPolygon_FewerThanThreeVerts_IsEmpty()
    {
        Assert.True(new ViewPolygon(new[] { new Vector2(0, 0), new Vector2(1, 0) }).IsEmpty);
        Assert.True(new ViewPolygon(System.Array.Empty<Vector2>()).IsEmpty);
    }

    [Fact]
    public void CellView_FullScreen_CoversNdc()
    {
        var v = CellView.FullScreen();
        Assert.False(v.IsEmpty);
        Assert.Equal(-1f, v.MinX, 5);
        Assert.Equal(1f, v.MaxX, 5);
        Assert.Equal(-1f, v.MinY, 5);
        Assert.Equal(1f, v.MaxY, 5);
    }

    [Fact]
    public void CellView_Add_GrowsUnionBoundsAndIsEmptyTracks()
    {
        var v = new CellView();
        Assert.True(v.IsEmpty);
        v.Add(new ViewPolygon(new[] { new Vector2(0, 0), new Vector2(0.2f, 0), new Vector2(0, 0.2f) }));
        v.Add(new ViewPolygon(new[] { new Vector2(-0.3f, -0.3f), new Vector2(-0.1f, -0.3f), new Vector2(-0.1f, -0.1f) }));
        Assert.False(v.IsEmpty);
        Assert.Equal(-0.3f, v.MinX, 5);
        Assert.Equal(0.2f, v.MaxX, 5);
    }
}
