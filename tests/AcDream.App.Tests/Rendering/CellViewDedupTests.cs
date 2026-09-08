using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class CellViewDedupTests
{
    private static ViewPolygon Quad(float ox, float oy) => new(new[]
    {
        new Vector2(ox - 0.5f, oy - 0.5f), new Vector2(ox + 0.5f, oy - 0.5f),
        new Vector2(ox + 0.5f, oy + 0.5f), new Vector2(ox - 0.5f, oy + 0.5f),
    });

    [Fact]
    public void Add_DropsSubGridDriftDuplicate()
    {
        var v = new CellView();
        Assert.True(v.Add(Quad(0f, 0f)));
        var drifted = new ViewPolygon(new[]
        {
            new Vector2(-0.5f + 3e-4f, -0.5f - 3e-4f), new Vector2(0.5f + 3e-4f, -0.5f - 3e-4f),
            new Vector2(0.5f + 3e-4f, 0.5f - 3e-4f), new Vector2(-0.5f + 3e-4f, 0.5f - 3e-4f),
        });
        Assert.False(v.Add(drifted));
        Assert.Single(v.Polygons);
    }

    [Fact]
    public void Add_DropsRotatedStartDuplicate()
    {
        var v = new CellView();
        Assert.True(v.Add(Quad(0f, 0f)));
        var rotated = new ViewPolygon(new[]
        {
            new Vector2(0.5f, -0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(-0.5f, 0.5f), new Vector2(-0.5f, -0.5f),
        });
        Assert.False(v.Add(rotated));
        Assert.Single(v.Polygons);
    }

    [Fact]
    public void Add_KeepsGenuinelyDistinctPolygons()
    {
        // The fix must NOT over-merge: two regions 0.4 NDC apart (far beyond the 1e-3 grid)
        // remain distinct, so a real second portal opening is not silently dropped.
        var v = new CellView();
        Assert.True(v.Add(Quad(0f, 0f)));
        Assert.True(v.Add(Quad(0.4f, 0f)));
        Assert.Equal(2, v.Polygons.Count);
    }

    [Fact]
    public void Add_RepeatedDuplicate_DoesNotAllocatePerProbe()
    {
        var view = new CellView();
        ViewPolygon polygon = Quad(0f, 0f);
        Assert.True(view.Add(polygon));

        // Warm the method/JIT before measuring. Duplicate portal emissions are the hot path.
        Assert.False(view.Add(polygon));
        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () => _ = view.Add(polygon),
            batchSize: 1_000);

        Assert.True(allocated <= 256, $"duplicate probes allocated {allocated:N0} bytes");
    }

    [Fact]
    public void ResetAndRebuild_ReusesCanonicalKeyStorage()
    {
        var view = new CellView();
        ViewPolygon polygon = Quad(0f, 0f);

        Assert.True(view.Add(polygon));
        view.Reset();
        Assert.True(view.Add(polygon));

        long allocated = ZeroAllocationProbe.MeasureWarmed(
            () =>
            {
                view.Reset();
                _ = view.Add(polygon);
            },
            batchSize: 1_000);

        Assert.True(allocated <= 256, $"steady rebuilds allocated {allocated:N0} bytes");
    }
}
