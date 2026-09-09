using AcDream.App.Rendering.Wb;
using Xunit;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class WbDrawDispatcherIndoorFlagTests
{
    // ── Spec §5 representative ids ─────────────────────────────────────────

    [Fact]
    public void EnvCell_AgenOfArcanum_IndoorFlag1()
    {
        uint envCell = 0xA9B40172u;
        Assert.True(WbDrawDispatcher.IndoorObjectReceivesTorches(envCell),
            "EnvCell 0xA9B40172 must be indoor (flag=1): objects here skip the sun.");
    }

    [Fact]
    public void LandSubCell_OutdoorFlag0()
    {
        uint landSubCell = 0xA9B40031u;
        Assert.False(WbDrawDispatcher.IndoorObjectReceivesTorches(landSubCell),
            "Land sub-cell 0xA9B40031 must be outdoor (flag=0): objects here get the sun.");
    }

    [Fact]
    public void LandblockId_OutdoorFlag0()
    {
        uint landblockId = 0xA9B4FFFFu;
        Assert.False(WbDrawDispatcher.IndoorObjectReceivesTorches(landblockId),
            "Landblock id 0xA9B4FFFF must be outdoor (flag=0): the 0xFFFF low-word is excluded.");
    }

    [Fact]
    public void NullParentCellId_OutdoorFlag0()
    {
        // Outdoor scenery / building exterior shells have no ParentCellId → outdoor
        Assert.False(WbDrawDispatcher.IndoorObjectReceivesTorches(null),
            "Null ParentCellId must be outdoor (flag=0): building shells get the sun, not torches.");
    }

    // ── Boundary: first valid EnvCell low word ──────────────────────────────

    [Fact]
    public void CellId_LowWord0x0100_IsIndoor()
    {
        // Low word exactly 0x0100 is the first indoor EnvCell id — just on the boundary
        uint firstEnvCell = 0xA9B40100u;
        Assert.True(WbDrawDispatcher.IndoorObjectReceivesTorches(firstEnvCell),
            "Low word 0x0100 is the lowest valid EnvCell id (inclusive boundary → indoor).");
    }

    [Fact]
    public void CellId_LowWord0x00FF_IsOutdoor()
    {
        // Low word 0x00FF is one below the EnvCell range — still outdoor
        uint lastLandSubCell = 0xA9B400FFu;
        Assert.False(WbDrawDispatcher.IndoorObjectReceivesTorches(lastLandSubCell),
            "Low word 0x00FF is just below the EnvCell range (exclusive boundary → outdoor).");
    }
}
