using System.Linq;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class MotionCommandCatalogDatTests
{
    /// <summary>
    /// True if any local MotionTable can actually animate
    /// <paramref name="fullCommand"/> — the from-state transition key
    /// (Links outer, low 16 bits), the animation-bearing inner key
    /// (Links[...].MotionData), or a Modifiers entry.
    /// </summary>
    private static bool ExistsInAnyMotionTable(DatCollection dats, uint fullCommand)
    {
        ushort low16 = (ushort)(fullCommand & 0xFFFFu);
        int fullAsInt = unchecked((int)fullCommand);

        foreach (var id in dats.GetAllIdsOfType<MotionTable>())
        {
            var mt = dats.Get<MotionTable>(id);
            if (mt is null) continue;

            bool outerLow = mt.Links.Keys.Any(k => (k & 0xFFFF) == low16);
            if (outerLow) return true;

            bool innerFull = mt.Links.Values.Any(md => md.MotionData.ContainsKey(fullAsInt));
            if (innerFull) return true;

            if (mt.Modifiers.ContainsKey(fullAsInt)) return true;
        }

        return false;
    }

    [Fact]
    public void AceShiftedRecallCommands_ExistInLocalMotionTables()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.True(ExistsInAnyMotionTable(dats, 0x10000153u), "LifestoneRecall (ACE 0x10000153) must exist in local DAT MotionTables");
        Assert.True(ExistsInAnyMotionTable(dats, 0x1000013Au), "HouseRecall (ACE 0x1000013A) must exist in local DAT MotionTables");
    }

    [Fact]
    public void TwoThousandThirteenLifestoneAndHouseRecall_AlsoExist()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.False(ExistsInAnyMotionTable(dats, 0x10000150u), "LifestoneRecall (2013 0x10000150) unexpectedly found");
        Assert.True(ExistsInAnyMotionTable(dats, 0x10000137u), "HouseRecall (2013 0x10000137) must also exist in local DAT MotionTables");
    }

    [Theory]
    [InlineData(0x10000166u)]
    [InlineData(0x10000171u)]
    [InlineData(0x10000173u)]
    public void AceOnlyCommands_ExistOnlyUnderShiftedIds(uint aceFullCommand)
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.True(ExistsInAnyMotionTable(dats, aceFullCommand),
            $"ACE value 0x{aceFullCommand:X8} must exist in local DAT MotionTables");
    }

    [Theory]
    [InlineData(0x10000163u)] // MarketplaceRecall (2013)
    [InlineData(0x1000016Eu)] // AllegianceHometownRecall (2013)
    [InlineData(0x10000170u)] // OffhandSlashHigh (2013)
    public void TwoThousandThirteenOnlyValues_HaveZeroLinkHits(uint retail2013FullCommand)
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.False(ExistsInAnyMotionTable(dats, retail2013FullCommand),
            $"2013 value 0x{retail2013FullCommand:X8} unexpectedly found in local DAT MotionTables");
    }
}
