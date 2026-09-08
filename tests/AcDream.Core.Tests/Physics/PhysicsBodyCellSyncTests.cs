using System.Numerics;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class PhysicsBodyCellSyncTests
{
    [Fact]
    public void SnapToCell_ConsistentOutdoorPair_SeedsAsGiven()
    {
        var body = new PhysicsBody();
        body.SnapToCell(0xC95B0023u, worldPos: new Vector3(1000f, 2000f, 12f), cellLocal: new Vector3(100f, 50f, 12f));
        Assert.Equal(0xC95B0023u, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(100f, 50f, 12f), body.CellPosition.Frame.Origin);
        Assert.Equal(new Vector3(1000f, 2000f, 12f), body.Position);
    }

    [Fact]
    public void SnapToCell_InconsistentOutdoorPair_CanonicalizesCellFromLocal()
    {
        var body = new PhysicsBody();
        body.SnapToCell(0xC95B0001u, new Vector3(1000f, 2000f, 12f), new Vector3(100f, 50f, 12f));
        Assert.Equal(0xC95B0023u, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(100f, 50f, 12f), body.CellPosition.Frame.Origin);
    }

    [Fact]
    public void PositionDelta_WithinLandblock_UpdatesCellIndex()
    {
        var body = new PhysicsBody();
        body.SnapToCell(0xC95B0023u, new Vector3(1000f, 2000f, 12f), new Vector3(100f, 50f, 12f));
        body.Position += new Vector3(0f, 24f, 0f);
        Assert.Equal(new Vector3(100f, 74f, 12f), body.CellPosition.Frame.Origin);
        // low word for (lx=4, ly=3): (ly&7) + ((lx&7)<<3) + 1 = 3 + 32 + 1 = 0x24
        Assert.Equal(0xC95B0024u, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(1000f, 2024f, 12f), body.Position); // world still authoritative + moved
    }

    [Fact]
    public void PositionDelta_AcrossSouthLandblockEdge_BumpsCellAndRewraps()
    {
        var body = new PhysicsBody();
        body.SnapToCell(0xC95B0001u, new Vector3(1000f, 2000f, 12f), new Vector3(100f, 0.3f, 12f));
        body.Position += new Vector3(0f, -1f, 0f);   // local Y → -0.7 → south neighbour, ~191.3
        int lbY = (int)((body.CellPosition.ObjCellId >> 16) & 0xFFu);
        Assert.Equal(0x5A, lbY);                                       // bumped to south neighbour 0xC95A
        Assert.InRange(body.CellPosition.Frame.Origin.Y, 190f, 192f);   // re-wrapped near top of southern block
    }

    [Fact]
    public void PositionDelta_WithoutSeed_LeavesCellPositionDefault()
    {
        var body = new PhysicsBody();
        body.Position += new Vector3(10f, 10f, 0f);
        Assert.Equal(0u, body.CellPosition.ObjCellId);
    }

    [Fact]
    public void PositionDelta_InsideEnvCell_AdvancesCanonicalLocalFrame()
    {
        var body = new PhysicsBody();
        body.SnapToCell(
            0x8C0401ADu,
            worldPos: new Vector3(12f, -30f, 4f),
            cellLocal: new Vector3(80f, 40f, 4f));

        body.Position += new Vector3(3f, -2f, 0.5f);

        Assert.Equal(0x8C0401ADu, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(83f, 38f, 4.5f), body.CellPosition.Frame.Origin);
    }

    [Fact]
    public void CommitTransitionPosition_InsideDungeon_AdoptsResolvedEnvCellAndFrame()
    {
        var body = new PhysicsBody();
        body.SnapToCell(
            0x8C0401ADu,
            worldPos: new Vector3(12f, -30f, 4f),
            cellLocal: new Vector3(80f, 40f, 4f));

        body.CommitTransitionPosition(
            0x8C0401AEu,
            new Vector3(17f, -27f, 4f));

        Assert.Equal(0x8C0401AEu, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(85f, 43f, 4f), body.CellPosition.Frame.Origin);
        Assert.Equal(new Vector3(17f, -27f, 4f), body.Position);
    }

    [Fact]
    public void CommitTransitionPosition_WithoutSeed_DoesNotInventCanonicalCell()
    {
        var body = new PhysicsBody();

        body.CommitTransitionPosition(0x8C0401ADu, new Vector3(1f, 2f, 3f));

        Assert.Equal(0u, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(1f, 2f, 3f), body.Position);
    }

    [Fact]
    public void ContinuousTracking_LongWalkAcrossCellsAndBlock_NeverGoesStale()
    {
        var body = new PhysicsBody();
        body.SnapToCell(0xC95B0001u, new Vector3(1000f, 2000f, 12f), new Vector3(10f, 10f, 12f));

        // A winding walk: east within the block, then north across the 192 m boundary.
        foreach (var step in new[]
        {
            new Vector3(50f, 0f, 0f),
            new Vector3(50f, 0f, 0f),
            new Vector3(0f, 100f, 0f),
            new Vector3(0f, 100f, 0f),
        })
            body.Position += step;

        Assert.Equal(0xC95C0021u, body.CellPosition.ObjCellId);
        Assert.Equal(new Vector3(110f, 18f, 12f), body.CellPosition.Frame.Origin);

        uint cell = body.CellPosition.ObjCellId;
        var local = body.CellPosition.Frame.Origin;
        Assert.True(LandDefs.AdjustToOutside(ref cell, ref local));
        Assert.Equal(body.CellPosition.ObjCellId, cell);
    }
}
