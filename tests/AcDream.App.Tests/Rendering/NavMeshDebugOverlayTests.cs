using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Navigation;

namespace AcDream.App.Tests.Rendering;

public sealed class NavMeshDebugOverlayTests
{
    private static readonly NavBody Body = new(0.48f, 1.835f, 0.4f, 0.4f);

    [Fact]
    public void EveryTightPointIsDrawnAndClearPointsOnlyOnEverySecondColumnAndRow()
    {
        // The floor's edges fall on odd columns and rows, which the stride skips.
        NavGrid grid = Build(Floor(0.25f, 0.25f, 4.5f, 4.5f, 0f));

        List<(Vector3 At, bool Clear)> marks = NavMeshDebugOverlay.GridMarks(grid, new Vector3(2.4f, 2.4f, 0f)).ToList();

        int tight = Enumerable.Range(0, grid.NodeCount).Count(node => !grid.IsClear(node));
        int clearOnStride = Enumerable.Range(0, grid.NodeCount)
            .Count(node => grid.IsClear(node) && OnStride(grid, grid.Position(node)));
        Assert.True(tight > 0);
        Assert.Equal(tight, marks.Count(mark => !mark.Clear));
        Assert.Contains(marks, mark => !mark.Clear && !OnStride(grid, mark.At));
        Assert.Equal(clearOnStride, marks.Count(mark => mark.Clear));
        Assert.All(marks.Where(mark => mark.Clear), mark => Assert.True(OnStride(grid, mark.At)));
    }

    [Fact]
    public void FloorsAboveAndBelowAreMarkedAndOnlyDistanceAcrossLeavesPointsOut()
    {
        NavGrid grid = Build(
        [
            .. Floor(1f, 1f, 7f, 7f, 0f),
            .. Floor(9f, 1f, 15f, 7f, 5f),
            .. Floor(1f, 9f, 7f, 15f, -5f),
            .. Floor(26f, 26f, 31f, 31f, 0f),
        ]);

        List<(Vector3 At, bool Clear)> marks = NavMeshDebugOverlay.GridMarks(grid, new Vector3(4f, 4f, 0f)).ToList();

        Assert.Contains(marks, mark => mark.At.Z > 4f);
        Assert.Contains(marks, mark => mark.At.Z < -4f);
        Assert.DoesNotContain(marks, mark => mark.At.X > 26f && mark.At.Y > 26f);
    }

    private static bool OnStride(NavGrid grid, Vector3 at) =>
        (int)MathF.Floor((at.X - grid.OriginX) / grid.CellSize) % NavMeshDebugOverlay.DrawStride == 0
        && (int)MathF.Floor((at.Y - grid.OriginY) / grid.CellSize) % NavMeshDebugOverlay.DrawStride == 0;

    private static NavGrid Build(NavTriangle[] triangles) =>
        NavGrid.Build(new NavGeometry(0f, 0f, 32f, [], triangles, [], []), Body);

    private static NavTriangle[] Floor(float x0, float y0, float x1, float y1, float z) =>
    [
        new NavTriangle(new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z)),
        new NavTriangle(new Vector3(x0, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z)),
    ];
}
