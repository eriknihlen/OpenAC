using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherTorchGateTests
{
    [Fact]
    public void BuildingShell_NullParent_IsOutdoor_NoTorches()
    {
        // Building exterior shells are top-level landblock stabs with no
        // ParentCellId (LandblockLoader sets BuildingShellAnchorCellId, not Parent).
        Assert.False(WbDrawDispatcher.IndoorObjectReceivesTorches(null));
    }

    [Theory]
    [InlineData(0xA9B4_0001u)]
    [InlineData(0xA9B4_0020u)]
    [InlineData(0xA9B4_0040u)]
    public void OutdoorLandCell_NoTorches(uint parentCellId)
    {
        Assert.False(WbDrawDispatcher.IndoorObjectReceivesTorches(parentCellId));
    }

    [Theory]
    [InlineData(0xA9B4_0100u)] // first EnvCell
    [InlineData(0xA9B4_0164u)] // interior EnvCell
    [InlineData(0x0007_0143u)] // dungeon EnvCell
    public void IndoorEnvCell_GetsTorches(uint parentCellId)
    {
        Assert.True(WbDrawDispatcher.IndoorObjectReceivesTorches(parentCellId));
    }
}
