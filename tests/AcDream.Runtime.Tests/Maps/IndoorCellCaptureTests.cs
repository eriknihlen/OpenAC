using System.Numerics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Maps;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// A plugin that draws dungeons from its own tiles needs each cell's
/// environment piece and placement exactly as the data stores them, for
/// every cell the landblock lists -- including a cell with no geometry the
/// floorplan leaves out.
///
/// Mutation check (2026-09-25): reading the orientation as identity turned
/// <see cref="EveryListedCellComesBackWithItsPieceAndPlacement"/> red.
/// </summary>
public sealed class IndoorCellCaptureTests
{
    [Fact]
    public void EveryListedCellComesBackWithItsPieceAndPlacement()
    {
        var content = new TwoRoomLandblock();
        var map = new RuntimeDungeonMapAutomation();
        map.BindContent(content, content.Lock);

        IReadOnlyList<PluginIndoorCell> cells =
            map.CaptureIndoorCells(TwoRoomLandblock.TurnedRoom);

        Assert.Equal(
            [TwoRoomLandblock.TurnedRoom, TwoRoomLandblock.UpstairsRoom, TwoRoomLandblock.CellSeeingOutside],
            cells.Select(static cell => cell.CellId));

        PluginIndoorCell turned = cells[0];
        Assert.Equal(0x0123u, turned.EnvironmentId);
        Assert.Equal(1u, turned.CellStructure);
        Assert.Equal(new Vector3(20f, 30f, 0.5f), turned.Origin);
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f), turned.Orientation);
        Assert.Equal(90f, turned.YawDegrees, 3);
        Assert.False(turned.SeesOutside);

        PluginIndoorCell upstairs = cells[1];
        Assert.Equal(2u, upstairs.CellStructure);
        Assert.Equal(new Vector3(40f, 30f, 7.2f), upstairs.Origin);
        Assert.Equal(0f, upstairs.YawDegrees, 3);

        PluginIndoorCell outside = cells[2];
        Assert.Equal(0u, outside.EnvironmentId);
        Assert.True(outside.SeesOutside);

        Assert.Equal(0, content.ReadsOutsideTheLock);
        int reads = content.Reads;
        Assert.Same(cells, map.CaptureIndoorCells(TwoRoomLandblock.Landblock));
        Assert.Equal(reads, content.Reads);
    }

    [Fact]
    public void ALandblockWithoutCellsOrWithoutFilesHasNone()
    {
        var content = new TwoRoomLandblock();
        var map = new RuntimeDungeonMapAutomation();
        Assert.Empty(map.CaptureIndoorCells(TwoRoomLandblock.Landblock));

        map.BindContent(content, content.Lock);
        Assert.Empty(map.CaptureIndoorCells(TwoRoomLandblock.EmptyLandblock));
        Assert.Empty(map.CaptureIndoorCells(0x7F7F0000u));
    }

    [Fact]
    public void TheSharedListCannotBeChangedByAPlugin()
    {
        var content = new TwoRoomLandblock();
        var map = new RuntimeDungeonMapAutomation();
        map.BindContent(content, content.Lock);

        IReadOnlyList<PluginIndoorCell> cells =
            map.CaptureIndoorCells(TwoRoomLandblock.Landblock);

        Assert.IsNotType<PluginIndoorCell[]>(cells);
        var writable = Assert.IsAssignableFrom<IList<PluginIndoorCell>>(cells);
        Assert.Throws<NotSupportedException>(() => writable[0] = default);
    }

    [Fact]
    public void TheLandblockReadLongestAgoIsDroppedOnceTheCacheIsFull()
    {
        var content = new TwoRoomLandblock();
        var map = new RuntimeDungeonMapAutomation();
        map.BindContent(content, content.Lock);
        IReadOnlyList<PluginIndoorCell> first =
            map.CaptureIndoorCells(TwoRoomLandblock.Landblock);

        for (uint index = 1; index < RuntimeDungeonMapAutomation.MaximumCachedIndoorLandblocks; index++)
            _ = map.CaptureIndoorCells(0x70000000u + (index << 16));
        Assert.Same(first, map.CaptureIndoorCells(TwoRoomLandblock.Landblock));

        _ = map.CaptureIndoorCells(0x6F000000u);
        IReadOnlyList<PluginIndoorCell> again =
            map.CaptureIndoorCells(TwoRoomLandblock.Landblock);
        Assert.NotSame(first, again);
        Assert.Equal(first, again);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(90f, 90f)]
    [InlineData(180f, 180f)]
    [InlineData(270f, 270f)]
    [InlineData(-90f, 270f)]
    public void YawIsCountedFromEastTowardsNorth(float turnDegrees, float expected)
    {
        var cell = new PluginIndoorCell(
            0x01230100u, 1u, 0u, Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turnDegrees * MathF.PI / 180f),
            false);
        Assert.Equal(expected, cell.YawDegrees, 2);
    }
}
