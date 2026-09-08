using System.Collections.Generic;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class RemoveCellsForLandblockTests
{
    private static CellPhysics BuildMinimalCell() => new()
    {
        Resolved = new Dictionary<ushort, ResolvedPolygon>(),
    };

    [Fact]
    public void RemoveCellsForLandblock_EvictsOnlyMatchingPrefix()
    {
        var cache = new PhysicsDataCache();

        cache.RegisterCellStructForTest(0xAAAA0100u, BuildMinimalCell());
        cache.RegisterCellStructForTest(0xAAAA0200u, BuildMinimalCell());
        cache.RegisterCellStructForTest(0xBBBB0100u, BuildMinimalCell());

        cache.RemoveCellsForLandblock(0xAAAA0000u);

        Assert.Null(cache.GetCellStruct(0xAAAA0100u));
        Assert.Null(cache.GetCellStruct(0xAAAA0200u));

        Assert.NotNull(cache.GetCellStruct(0xBBBB0100u));
    }

    [Fact]
    public void RemoveCellsForLandblock_EmptyCache_DoesNotThrow()
    {
        var cache = new PhysicsDataCache();
        cache.RemoveCellsForLandblock(0xAAAA0000u);
        Assert.Equal(0, cache.CellStructCount);
    }

    [Fact]
    public void RemoveCellsForLandblock_NoMatchingCells_LeavesOthersIntact()
    {
        var cache = new PhysicsDataCache();
        cache.RegisterCellStructForTest(0xBBBB0100u, BuildMinimalCell());

        cache.RemoveCellsForLandblock(0xAAAA0000u);

        Assert.NotNull(cache.GetCellStruct(0xBBBB0100u));
        Assert.Equal(1, cache.CellStructCount);
    }
}
