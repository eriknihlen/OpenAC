using System.Linq;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatLandBlock = DatReaderWriter.DBObjs.LandBlock;
using DatLandBlockInfo = DatReaderWriter.DBObjs.LandBlockInfo;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "InstalledDat")]
public sealed class DungeonLandblockDatProbeTests
{
    private readonly ITestOutputHelper _out;
    public DungeonLandblockDatProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe_Dungeon0125_vs_Holtburg_A9B4()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint lb in new uint[] { 0x0125u, 0xA9B4u })
        {
            _out.WriteLine($"=== landblock 0x{lb:X4} ===");

            uint terrainId = (lb << 16) | 0xFFFFu;
            var block = dats.Get<DatLandBlock>(terrainId);
            if (block is null)
            {
                _out.WriteLine($"  LandBlock 0x{terrainId:X8}: NULL (no terrain record)");
            }
            else
            {
                var heights = block.Height;
                bool allZero = heights is not null && heights.All(h => h == 0);
                int distinct = heights is null ? 0 : heights.Distinct().Count();
                _out.WriteLine($"  LandBlock 0x{terrainId:X8}: present, Height[{heights?.Length ?? 0}] allZero={allZero} distinctIndices={distinct} first8=[{(heights is null ? "" : string.Join(",", heights.Take(8)))}]");
            }

            uint infoId = (lb << 16) | 0xFFFEu;
            var info = dats.Get<DatLandBlockInfo>(infoId);
            if (info is null)
            {
                _out.WriteLine($"  LandBlockInfo 0x{infoId:X8}: NULL");
            }
            else
            {
                _out.WriteLine($"  LandBlockInfo 0x{infoId:X8}: NumCells={info.NumCells} Buildings={info.Buildings?.Count ?? 0} Objects={info.Objects?.Count ?? 0}");
            }

            // probe the first few EnvCells
            int found = 0;
            for (uint low = 0x0100u; low < 0x0110u; low++)
            {
                uint cellId = (lb << 16) | low;
                var cell = dats.Get<DatEnvCell>(cellId);
                if (cell is not null)
                {
                    found++;
                    if (found <= 3)
                        _out.WriteLine($"  EnvCell 0x{cellId:X8}: present, CellStructure={cell.CellStructure} Portals={cell.CellPortals?.Count ?? 0} pos=({cell.Position.Origin.X:F1},{cell.Position.Origin.Y:F1},{cell.Position.Origin.Z:F1})");
                }
            }
            _out.WriteLine($"  EnvCells 0x0100..0x010F present: {found}");
        }
    }
}
