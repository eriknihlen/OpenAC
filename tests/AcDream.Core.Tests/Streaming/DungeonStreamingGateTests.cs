using AcDream.App.Streaming;
using Xunit;

namespace AcDream.Core.Tests.Streaming;

public class DungeonStreamingGateTests
{
    private const uint DungeonCell = 0x00070145u;
    private const uint OutdoorCell = 0xAB340001u;

    [Fact]
    public void SealedDungeonCell_NotHold_CollapsesAndPinsObserverToCellLandblock()
    {
        var r = DungeonStreamingGate.Compute(
            isTeleportHold: false, currCellIsSealedDungeon: true, currCellId: DungeonCell);

        Assert.True(r.InsideDungeon);
        Assert.Equal(0x0007u, r.ObserverLandblockKey);
    }

    [Fact]
    public void OutdoorCell_NotHold_NoCollapse_NoObserverOverride()
    {
        var r = DungeonStreamingGate.Compute(
            isTeleportHold: false, currCellIsSealedDungeon: false, currCellId: OutdoorCell);

        Assert.False(r.InsideDungeon);
        Assert.Null(r.ObserverLandblockKey);
    }

    [Fact]
    public void TeleportHold_StaleSealedDungeonCurrCell_SuppressesGate()
    {
        var r = DungeonStreamingGate.Compute(
            isTeleportHold: true, currCellIsSealedDungeon: true, currCellId: DungeonCell);

        Assert.False(r.InsideDungeon);
        Assert.Null(r.ObserverLandblockKey);
    }

    [Fact]
    public void TeleportHold_OutdoorCurrCell_AlsoNoCollapse()
    {
        var r = DungeonStreamingGate.Compute(
            isTeleportHold: true, currCellIsSealedDungeon: false, currCellId: OutdoorCell);

        Assert.False(r.InsideDungeon);
        Assert.Null(r.ObserverLandblockKey);
    }
}
