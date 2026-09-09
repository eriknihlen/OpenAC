using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public class CellVisibilityCommitTests
{
    [Fact]
    public void CommitLandblock_ReplacesOnlyThatLandblocksCompleteCellSet()
    {
        var visibility = new CellVisibility();
        visibility.CommitLandblock(0x8C04FFFFu, new[]
        {
            Cell(0x8C040100u),
            Cell(0x8C040101u),
        });
        visibility.CommitLandblock(0x0007FFFFu, new[] { Cell(0x00070100u) });

        visibility.CommitLandblock(0x8C04FFFFu, new[] { Cell(0x8C040200u) });

        Assert.False(visibility.TryGetCell(0x8C040100u, out _));
        Assert.False(visibility.TryGetCell(0x8C040101u, out _));
        Assert.True(visibility.TryGetCell(0x8C040200u, out _));
        Assert.True(visibility.TryGetCell(0x00070100u, out _));
        Assert.Single(visibility.GetCellsForLandblock(0x8C04u));
    }

    [Fact]
    public void CommitLandblock_RejectsForeignCellBeforeChangingCurrentState()
    {
        var visibility = new CellVisibility();
        visibility.CommitLandblock(0x8C04FFFFu, new[] { Cell(0x8C040100u) });

        Assert.Throws<ArgumentException>(() =>
            visibility.CommitLandblock(0x8C04FFFFu, new[] { Cell(0x00070100u) }));
        Assert.True(visibility.TryGetCell(0x8C040100u, out _));
    }

    private static LoadedCell Cell(uint id) => new() { CellId = id };
}
