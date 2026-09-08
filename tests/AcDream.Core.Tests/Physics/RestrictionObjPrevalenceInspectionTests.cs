using System.Collections.Generic;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class RestrictionObjPrevalenceInspectionTests
{
    private readonly ITestOutputHelper _output;

    public RestrictionObjPrevalenceInspectionTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public void CountRestrictionObjCells_InstalledCellDat()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        // Bake-tool enumeration shape (BakeRunner.EnumerateEnvCellIds):
        // LandBlockInfo (xxxxFFFE) NumCells ranges name every EnvCell id.
        var landblockInfoIds = new List<uint>();
        foreach (var file in dats.Cell.Tree)
        {
            if ((file.Id & 0xFFFFu) == 0xFFFEu)
                landblockInfoIds.Add(file.Id);
        }

        int totalCells = 0;
        int restricted = 0;
        var restrictedLandblocks = new HashSet<uint>();
        var samples = new List<string>();

        foreach (uint infoId in landblockInfoIds)
        {
            if (!dats.Cell.TryGet<DatReaderWriter.DBObjs.LandBlockInfo>(
                    infoId, out var info)
                || info is null
                || info.NumCells == 0)
            {
                continue;
            }

            uint firstCellId = (infoId & 0xFFFF_0000u) | 0x0100u;
            for (uint offset = 0; offset < info.NumCells; offset++)
            {
                uint cellId = firstCellId + offset;
                if (!dats.Cell.TryGet<DatReaderWriter.DBObjs.EnvCell>(
                        cellId, out var cell)
                    || cell is null)
                {
                    continue;
                }

                totalCells++;
                if (cell.RestrictionObj != 0)
                {
                    restricted++;
                    restrictedLandblocks.Add(cellId & 0xFFFF_0000u);
                    if (samples.Count < 40)
                    {
                        samples.Add(
                            $"cell 0x{cellId:X8} restrictionObj 0x{cell.RestrictionObj:X8}");
                    }
                }
            }
        }

        _output.WriteLine(
            $"EnvCells scanned: {totalCells}; with RestrictionObj != 0: "
            + $"{restricted}; distinct landblocks: {restrictedLandblocks.Count}");
        foreach (string s in samples)
            _output.WriteLine(s);

        Assert.True(totalCells > 0, "installed cell DAT enumerated no EnvCells");
    }
}
