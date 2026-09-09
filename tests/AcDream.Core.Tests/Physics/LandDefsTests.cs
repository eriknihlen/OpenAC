using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class LandDefsTests
{
    // ── blockid_to_lcoord (pc:68520) ────────────────────────────────────

    [Fact]
    public void BlockIdToLcoord_A9B4_Is_1352_1440()
    {
        Assert.True(LandDefs.BlockIdToLcoord(0xA9B40031u, out int lx, out int ly));
        Assert.Equal(169 * 8, lx);
        Assert.Equal(180 * 8, ly);
    }

    [Fact]
    public void BlockIdToLcoord_HighBlockY_NoSignExtension()
    {
        Assert.True(LandDefs.BlockIdToLcoord(0xFEFE0001u, out int lx, out int ly));
        Assert.Equal(0xFE * 8, lx);
        Assert.Equal(0xFE * 8, ly);
    }

    [Fact]
    public void BlockIdToLcoord_ZeroCellId_Fails()
    {
        Assert.False(LandDefs.BlockIdToLcoord(0u, out _, out _));
    }

    // ── gid_to_lcoord (pc:163500) ───────────────────────────────────────

    [Fact]
    public void GidToLcoord_A9B40031_Is_1358_1440()
    {
        Assert.True(LandDefs.GidToLcoord(0xA9B40031u, out int lx, out int ly));
        Assert.Equal(1358, lx);
        Assert.Equal(1440, ly);
    }

    [Fact]
    public void GidToLcoord_IndoorCell_Fails()
    {
        Assert.False(LandDefs.GidToLcoord(0xA9B40164u, out _, out _));
    }

    [Fact]
    public void GidToLcoord_InvalidLowRange_Fails()
    {
        Assert.False(LandDefs.GidToLcoord(0xA9B40000u, out _, out _)); // low 0
        Assert.False(LandDefs.GidToLcoord(0xA9B40041u, out _, out _)); // low 0x41 (> 0x40, < 0x100)
    }

    // ── lcoord_to_gid (pc:171859) ───────────────────────────────────────

    [Fact]
    public void LcoordToGid_RoundTrips_A9B40031()
    {
        Assert.Equal(0xA9B40031u, LandDefs.LcoordToGid(1358, 1440));
    }

    [Fact]
    public void LcoordToGid_CrossesBlockSouth()
    {
        Assert.Equal(0xA9B30038u, LandDefs.LcoordToGid(1358, 1439));
    }

    [Fact]
    public void LcoordToGid_OutOfMapBounds_ReturnsZero()
    {
        Assert.Equal(0u, LandDefs.LcoordToGid(-1, 5));
        Assert.Equal(0u, LandDefs.LcoordToGid(5, -1));
        Assert.Equal(0u, LandDefs.LcoordToGid(0x7F8, 5));
        Assert.Equal(0u, LandDefs.LcoordToGid(5, 0x7F8));
    }

    // ── get_outside_lcoord (pc:438690) ──────────────────────────────────

    [Fact]
    public void GetOutsideLcoord_NegativeLocalY_CrossesSouth()
    {
        // floor(-1/24) = -1 (floor, not truncation — negative-safe).
        Assert.True(LandDefs.GetOutsideLcoord(
            0xA9B40031u, new Vector3(150f, -1f, 0f), out int lx, out int ly));
        Assert.Equal(1358, lx);
        Assert.Equal(1439, ly);
    }

    [Fact]
    public void GetOutsideLcoord_FromIndoorCellId_UsesBlockBits()
    {
        // adjust_to_outside accepts indoor low16 (0x100..0xFFFD) — the block
        // bits drive the lcoord; the position picks the landcell.
        Assert.True(LandDefs.GetOutsideLcoord(
            0xA9B40164u, new Vector3(12f, 12f, 0f), out int lx, out int ly));
        Assert.Equal(1352, lx);
        Assert.Equal(1440, ly);
    }

    // ── adjust_to_outside (pc:438719) ───────────────────────────────────

    [Fact]
    public void AdjustToOutside_SouthCrossing_RewritesCellAndRebasesPos()
    {
        uint cellId = 0xA9B40031u;
        var pos = new Vector3(150f, -1f, 5f);
        Assert.True(LandDefs.AdjustToOutside(ref cellId, ref pos));
        Assert.Equal(0xA9B30038u, cellId);
        Assert.Equal(150f, pos.X, 4);
        Assert.Equal(191f, pos.Y, 4);   // -1 − floor(-1/192)·192 = 191 (new block's frame)
        Assert.Equal(5f, pos.Z, 4);     // Z untouched
    }

    [Fact]
    public void AdjustToOutside_DeepSouth_CaptureGolden()
    {
        uint cellId = 0xA9B40031u;
        var pos = new Vector3(150f, -109.65f, 0f);
        Assert.True(LandDefs.AdjustToOutside(ref cellId, ref pos));
        Assert.Equal(0xA9B30034u, cellId);
        Assert.Equal(82.35f, pos.Y, 3);
    }

    [Fact]
    public void AdjustToOutside_NorthboundReturn()
    {
        // From A9B3's frame, local y = 193 is 1 m into A9B4.
        uint cellId = 0xA9B30038u;
        var pos = new Vector3(150f, 193f, 0f);
        Assert.True(LandDefs.AdjustToOutside(ref cellId, ref pos));
        Assert.Equal(0xA9B40031u, cellId);
        Assert.Equal(1f, pos.Y, 4);
    }

    [Fact]
    public void AdjustToOutside_WithinBlock_KeepsBlockRewritesCell()
    {
        uint cellId = 0xA9B40031u;
        var pos = new Vector3(12f, 12f, 0f);
        Assert.True(LandDefs.AdjustToOutside(ref cellId, ref pos));
        Assert.Equal(0xA9B40001u, cellId);
        Assert.Equal(12f, pos.X, 4);
        Assert.Equal(12f, pos.Y, 4);
    }

    [Fact]
    public void AdjustToOutside_EpsilonSnap_TreatsTinyNegativeAsZero()
    {
        uint cellId = 0xA9B40031u;
        var pos = new Vector3(150f, -0.0001f, 0f);
        Assert.True(LandDefs.AdjustToOutside(ref cellId, ref pos));
        Assert.Equal(0xA9B40031u, cellId);
        Assert.Equal(0f, pos.Y, 6);
    }

    [Fact]
    public void AdjustToOutside_InvalidLow_FailsAndZeroesCell()
    {
        uint cellId = 0xA9B40050u;   // low 0x50: not landcell, not envcell
        var pos = new Vector3(12f, 12f, 0f);
        Assert.False(LandDefs.AdjustToOutside(ref cellId, ref pos));
        Assert.Equal(0u, cellId);
    }

    // ── inbound_valid_cellid (pc:163438) ────────────────────────────────

    [Theory]
    [InlineData(0xA9B40001u, true)]
    [InlineData(0xA9B40040u, true)]
    [InlineData(0xA9B40100u, true)]
    [InlineData(0xA9B4FFFDu, true)]
    [InlineData(0xA9B4FFFFu, true)]   // block sentinel
    [InlineData(0xA9B40000u, false)]
    [InlineData(0xA9B40041u, false)]
    [InlineData(0xA9B4FFFEu, false)]
    public void InboundValidCellId_LowRanges(uint cellId, bool expected)
    {
        Assert.Equal(expected, LandDefs.InboundValidCellId(cellId));
    }
}
