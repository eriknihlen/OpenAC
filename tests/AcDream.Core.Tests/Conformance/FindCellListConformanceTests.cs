using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.Core.Tests.Conformance;

public class FindCellListConformanceTests
{
    private const float FootRadius = 0.4f; // player foot-sphere radius

    private static PhysicsDataCache LoadThresholdBuilding(DatCollection dats)
    {
        var cache = new PhysicsDataCache();
        for (uint low = 0x016Fu; low <= 0x0175u; low++)
            ConformanceDats.LoadEnvCell(dats, cache, ConformanceDats.HoltburgLandblock | low);
        return cache;
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void FindCellList_DeepInsideRoom0171_Returns0171()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadThresholdBuilding(dats);

        var world = Vector3.Transform(
            CottageDoorwayCharacterizationTests.Interior0171Local,
            cache.GetCellStruct(CottageDoorwayCharacterizationTests.Room0171)!.WorldTransform);

        uint picked = CellTransit.FindCellList(cache, world, FootRadius,
            CottageDoorwayCharacterizationTests.Room0171);
        Assert.Equal(CottageDoorwayCharacterizationTests.Room0171, picked);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void FindCellList_DeepInsideVestibule0170_Returns0170()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadThresholdBuilding(dats);

        var world = Vector3.Transform(
            CottageDoorwayCharacterizationTests.Interior0170Local,
            cache.GetCellStruct(CottageDoorwayCharacterizationTests.Vestibule0170)!.WorldTransform);

        uint picked = CellTransit.FindCellList(cache, world, FootRadius,
            CottageDoorwayCharacterizationTests.Vestibule0170);
        Assert.Equal(CottageDoorwayCharacterizationTests.Vestibule0170, picked);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void FindCellList_InVestibule_SeededFromRoom_Returns0170()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadThresholdBuilding(dats);

        var world = Vector3.Transform(
            CottageDoorwayCharacterizationTests.Interior0170Local,
            cache.GetCellStruct(CottageDoorwayCharacterizationTests.Vestibule0170)!.WorldTransform);

        uint picked = CellTransit.FindCellList(cache, world, FootRadius,
            CottageDoorwayCharacterizationTests.Room0171); // stale/wrong seed
        Assert.Equal(CottageDoorwayCharacterizationTests.Vestibule0170, picked);
    }

    private static (PhysicsDataCache?, System.Collections.Generic.IReadOnlyList<RetailCellPick>?) LoadThresholdGolden()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) return (null, null);
        var fixturePath = System.IO.Path.Combine(ConformanceDats.FixturesDir, "find-cell-list-threshold.log");
        if (!System.IO.File.Exists(fixturePath)) return (null, null);

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = LoadThresholdBuilding(dats);
        var picks = RetailTrace.ParseAll(System.IO.File.ReadAllLines(fixturePath));
        return (cache, picks);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void FindCellList_DoorwayThreshold_IndoorPicks_MatchRetail()
    {
        var (cache, picks) = LoadThresholdGolden();
        if (cache is null) return;
        Assert.NotEmpty(picks!);

        var indoor = picks!.Where(p =>
            (p.SeedCellId & 0xFFFFu) >= 0x100u && (p.PickedCellId & 0xFFFFu) >= 0x100u).ToList();
        Assert.NotEmpty(indoor);

        foreach (var p in indoor)
        {
            uint got = CellTransit.FindCellList(cache, p.Position, FootRadius, p.SeedCellId);
            Assert.Equal(p.PickedCellId, got);
        }
    }
}
