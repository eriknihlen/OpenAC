using AcDream.App.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.World;

public sealed class LiveEntityLivenessControllerTests
{
    [Fact]
    public void OutOfRangeWorldEntityExpiresAfterTwentyFiveSeconds()
    {
        var tracker = new LiveEntityLivenessTracker();
        var samples = new[] { Sample(0x7000_0001u, generation: 4, visible: false) };

        Assert.Empty(tracker.Tick(10.0, samples));
        Assert.Empty(tracker.Tick(34.999, samples));
        Assert.Equal(
            new LiveEntityPruneCandidate(
                new RuntimeEntityKey(0x7000_0001u, 4),
                0x7000_0001u),
            Assert.Single(tracker.Tick(35.0, samples)));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReturningToVisibilityCancelsTheDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(20.0, [Sample(1, 1, visible: true)]));
        Assert.Empty(tracker.Tick(40.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(64.9, [Sample(1, 1, visible: false)]));
        Assert.Single(tracker.Tick(65.0, [Sample(1, 1, visible: false)]));
    }

    [Fact]
    public void NonWorldRetentionCancelsTheDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(30.0, [Sample(1, 1, visible: false, retained: true)]));
        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void ReusedGuidGetsANewGenerationDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));
        Assert.Empty(tracker.Tick(24.0, [Sample(1, 2, visible: false)]));
        Assert.Empty(tracker.Tick(25.0, [Sample(1, 2, visible: false)]));
        Assert.Equal(
            new LiveEntityPruneCandidate(new RuntimeEntityKey(1, 2), 1),
            Assert.Single(tracker.Tick(49.0, [Sample(1, 2, visible: false)])));
    }

    [Fact]
    public void RemovedRecordDropsItsDeadline()
    {
        var tracker = new LiveEntityLivenessTracker();
        Assert.Empty(tracker.Tick(0.0, [Sample(1, 1, visible: false)]));

        Assert.Empty(tracker.Tick(30.0, []));

        Assert.Equal(0, tracker.DeadlineCount);
    }

    [Fact]
    public void VisibilityIsThePlayersLandblockAndItsEightNeighbours()
    {
        const uint player = 0x3032_0001u;

        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3032_00A7u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3132_0001u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x2F31_0001u));
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3133_00FFu));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3232_0001u));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x3034_0001u));
        Assert.False(LiveEntityLivenessController.IsWithinVisibleLandblocks(player, 0x2E30_0001u));
    }

    [Fact]
    public void OutdoorsVisibilityIsTheNeighbourhood()
    {
        const uint player = 0x3032_0001u;

        Assert.True(LiveEntityLivenessController.IsVisibleFrom(player, 0x3032_00A7u, NoEnvCells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(player, 0x3133_00FFu, NoEnvCells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(player, 0x3232_0001u, NoEnvCells));
    }

    [Fact]
    public void VisibilityInsideADungeonIsTheCellsPvsList()
    {
        // The server holds a dungeon object only while its cell is the
        // player's cell or on that cell's PVS list; the rest of the same
        // landblock is out of sight, and a sealed cell sees no neighbour.
        const uint playerCell = 0x6145_031Du;
        var cells = EnvCells(
            (playerCell, false, [0x6145_0317u, 0x6145_0370u]),
            (0x6145_02B6u, false, []));

        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, playerCell, cells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x6145_0317u, cells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x6145_0370u, cells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x6145_02B6u, cells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x6145_0140u, cells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x6245_031Du, cells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x6145_0001u, cells));
    }

    [Fact]
    public void ACellSeenFromOutsideAlsoSeesTheNeighbourhood()
    {
        const uint playerCell = 0x3032_0101u;
        var cells = EnvCells(
            (playerCell, true, [0x3032_0102u]),
            (0x3032_0103u, false, []),
            (0x3032_0104u, true, []));

        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x3032_0102u, cells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x3032_0001u, cells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x3133_0001u, cells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x3032_0103u, cells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(playerCell, 0x3234_0001u, cells));

        // From outdoors, an object inside a building is seen only through a
        // cell that is itself seen from outside.
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(0x3032_0001u, 0x3032_0104u, cells));
        Assert.False(LiveEntityLivenessController.IsVisibleFrom(0x3032_0001u, 0x3032_0103u, cells));
    }

    [Fact]
    public void AnUnloadedEnvironmentCellCannotExpireAnything()
    {
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(0x6145_031Du, 0x6145_02B6u, NoEnvCells));
        Assert.True(LiveEntityLivenessController.IsVisibleFrom(0x3032_0001u, 0x3032_0103u, NoEnvCells));
    }

    private static readonly ILiveEntityEnvCellSource NoEnvCells = new EnvCellSource();

    private static ILiveEntityEnvCellSource EnvCells(
        params (uint CellId, bool SeenOutside, uint[] VisibleCellIds)[] cells)
    {
        var source = new EnvCellSource();
        foreach ((uint cellId, bool seenOutside, uint[] visibleCellIds) in cells)
        {
            source.Cells[cellId] = new LiveEntityEnvCellVisibility(
                seenOutside,
                new HashSet<uint>(visibleCellIds));
        }
        return source;
    }

    private sealed class EnvCellSource : ILiveEntityEnvCellSource
    {
        public Dictionary<uint, LiveEntityEnvCellVisibility> Cells { get; } = [];

        public LiveEntityEnvCellVisibility? GetEnvCell(uint cellId) =>
            Cells.TryGetValue(cellId, out LiveEntityEnvCellVisibility cell) ? cell : null;
    }

    [Fact]
    public void VisibilityDoesNotDependOnDistanceInsideTheNeighbourhood()
    {
        // The far corner of a diagonal neighbour is ~543 m away and used to
        // fall outside the old 384 m sphere while the server still knew it.
        Assert.True(LiveEntityLivenessController.IsWithinVisibleLandblocks(0x3032_0001u, 0x3133_0001u));
    }

    private static LiveEntityLivenessSample Sample(
        uint guid,
        ushort generation,
        bool visible,
        bool retained = false) =>
        new(
            new RuntimeEntityKey(guid, generation),
            guid,
            visible,
            retained);
}
