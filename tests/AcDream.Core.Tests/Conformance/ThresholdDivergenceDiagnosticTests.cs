using System.Linq;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "InstalledDat")]
public class ThresholdDivergenceDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public ThresholdDivergenceDiagnosticTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Diagnose_ThresholdTransitions()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        var fixturePath = System.IO.Path.Combine(ConformanceDats.FixturesDir, "find-cell-list-threshold.log");
        if (!System.IO.File.Exists(fixturePath)) { _out.WriteLine("SKIP: capture pending"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();
        var cells = new System.Collections.Generic.Dictionary<uint, AcDream.Core.World.Cells.EnvCell>();
        for (uint low = 0x016Fu; low <= 0x0175u; low++)
        {
            uint id = ConformanceDats.HoltburgLandblock | low;
            cells[id] = ConformanceDats.LoadEnvCell(dats, cache, id);
        }

        bool In(uint id, System.Numerics.Vector3 p) =>
            cells.TryGetValue(id, out var c) && c.PointInCell(p);

        foreach (var pick in RetailTrace.ParseAll(System.IO.File.ReadAllLines(fixturePath)))
        {
            uint ours = CellTransit.FindCellList(cache, pick.Position, 0.4f, pick.SeedCellId);
            _out.WriteLine(
                $"seed=0x{pick.SeedCellId & 0xFFFF:X4} pos=({pick.Position.X:F2},{pick.Position.Y:F2},{pick.Position.Z:F2}) " +
                $"retail=0x{pick.PickedCellId & 0xFFFF:X4} acdream=0x{ours & 0xFFFF:X4} " +
                $"{(ours == pick.PickedCellId ? "MATCH" : "DIVERGE")} | " +
                $"inSeed={(In(pick.SeedCellId, pick.Position) ? 1 : 0)} " +
                $"in0170={(In(0xA9B40170u, pick.Position) ? 1 : 0)} " +
                $"in0171={(In(0xA9B40171u, pick.Position) ? 1 : 0)}");
        }
    }
}
