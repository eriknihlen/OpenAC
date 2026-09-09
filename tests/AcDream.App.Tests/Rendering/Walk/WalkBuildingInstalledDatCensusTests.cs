using AcDream.App.Tests.Rendering;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Rendering.Walk;

[Trait("Lane", "InstalledDat")]
public sealed class WalkBuildingInstalledDatCensusTests
{
    [Fact]
    public void CompleteInstalledBuildingCorpusMatchesBoundedSelectionCensus()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory.");
        using var dats = new DatCollection(datDir!, DatAccessType.Read);

        int landblocks = 0;
        int instances = 0;
        int ladderInstances = 0;
        int ladderSlots = 0;
        int nonzeroSlots = 0;
        int zeroFinalSlots = 0;
        var models = new HashSet<uint>();
        var ladderModels = new HashSet<uint>();
        var ladderHistogram = new Dictionary<int, int>();

        for (uint blockX = 0; blockX <= 0xFF; blockX++)
        for (uint blockY = 0; blockY <= 0xFF; blockY++)
        {
            uint infoId = (blockX << 24) | (blockY << 16) | 0xFFFEu;
            LandBlockInfo? info = dats.Get<LandBlockInfo>(infoId);
            if (info?.Buildings is not { Count: > 0 } buildings)
                continue;
            landblocks++;
            var anchors = new HashSet<uint>();
            uint landblock = infoId & 0xFFFF0000u;

            foreach (BuildingInfo building in buildings)
            {
                instances++;
                models.Add(building.ModelId);
                GfxObj? baseGfx = dats.Get<GfxObj>(building.ModelId);
                Assert.NotNull(baseGfx);

                BuildingPortal? firstInteriorPortal = building.Portals.FirstOrDefault(
                    static portal => portal.OtherCellId != 0xFFFF);
                int cellX = (int)MathF.Floor(building.Frame.Origin.X / 24f);
                int cellY = (int)MathF.Floor(building.Frame.Origin.Y / 24f);
                uint placementCell = landblock | (uint)(cellX * 8 + cellY + 1);
                uint anchor = firstInteriorPortal is null
                    ? placementCell
                    : landblock | firstInteriorPortal.OtherCellId;
                Assert.True(anchors.Add(anchor),
                    $"duplicate building anchor 0x{anchor:X8}");

                if (baseGfx!.DIDDegrade == 0)
                    continue;
                GfxObjDegradeInfo? ladder =
                    dats.Get<GfxObjDegradeInfo>(baseGfx.DIDDegrade);
                Assert.NotNull(ladder);
                ladderInstances++;
                ladderModels.Add(building.ModelId);
                int count = ladder!.Degrades.Count;
                ladderSlots += count;
                ladderHistogram[count] = ladderHistogram.GetValueOrDefault(count) + 1;
                Assert.NotEmpty(ladder.Degrades);
                Assert.Equal(0u, (uint)ladder.Degrades[^1].Id);
                zeroFinalSlots++;

                for (int i = 0; i < ladder.Degrades.Count - 1; i++)
                {
                    GfxObjInfo slot = ladder.Degrades[i];
                    Assert.NotEqual(0u, (uint)slot.Id);
                    GfxObj? selected = dats.Get<GfxObj>((uint)slot.Id);
                    Assert.NotNull(selected);
                    Assert.NotNull(selected!.DrawingBSP?.Root);
                    nonzeroSlots++;
                }
            }
        }

        Assert.Equal(1_639, landblocks);
        Assert.Equal(6_979, instances);
        Assert.Equal(398, models.Count);
        Assert.Equal(6_760, ladderInstances);
        Assert.Equal(27_859, ladderSlots);
        Assert.Equal(350, ladderModels.Count);
        Assert.Equal(6_760, zeroFinalSlots);
        Assert.Equal(21_099, nonzeroSlots);
        Assert.Equal(
            new Dictionary<int, int>
            {
                [2] = 196,
                [3] = 216,
                [4] = 4_964,
                [5] = 1_341,
                [6] = 43,
            },
            ladderHistogram);
    }
}
