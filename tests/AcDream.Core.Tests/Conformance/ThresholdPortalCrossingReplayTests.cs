using System;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public class ThresholdPortalCrossingReplayTests
{
    private readonly ITestOutputHelper _out;
    public ThresholdPortalCrossingReplayTests(ITestOutputHelper output) => _out = output;

    private const float FloorZ        = 94.005f;
    private const float SphereRadius  = 0.4f;
    private const float SphereHeight  = 1.2f;
    private const float StepUpHeight  = 0.4f;
    private const float StepDownHeight = 0.1f;

    private static (PhysicsEngine, PhysicsDataCache) BuildBuildingEngine(DatCollection dats)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        for (uint low = 0x016Fu; low <= 0x0175u; low++)
            ConformanceDats.LoadEnvCell(dats, cache, ConformanceDats.HoltburgLandblock | low);

        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(0xA9B40000u, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return (engine, cache);
    }

    private static PhysicsBody GroundedBodyAt(Vector3 pos, uint cellId) => new()
    {
        Position             = pos,
        Orientation          = Quaternion.Identity,
        ContactPlaneValid    = true,
        ContactPlane         = new Plane(0f, 0f, 1f, -FloorZ),
        ContactPlaneCellId   = cellId,
        WalkablePolygonValid = true,
        WalkablePlane        = new Plane(0f, 0f, 1f, -FloorZ),
        WalkableVertices     = new[]
        {
            new Vector3(150f,  5f, FloorZ), new Vector3(150f, 20f, FloorZ),
            new Vector3(165f, 20f, FloorZ), new Vector3(165f,  5f, FloorZ),
        },
        WalkableUp           = Vector3.UnitZ,
        TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
    };

    private static uint Low(uint id) => id & 0xFFFFu;

    [Fact]
    public void ProductionPath_IndoorCrossings_MatchRetail()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        var fixturePath = Path.Combine(ConformanceDats.FixturesDir, "find-cell-list-threshold.log");
        if (!File.Exists(fixturePath)) { _out.WriteLine("SKIP: capture pending"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var (engine, _) = BuildBuildingEngine(dats);
        var picks = RetailTrace.ParseAll(File.ReadAllLines(fixturePath));

        int match = 0, total = 0;
        var failures = new System.Collections.Generic.List<string>();
        for (int i = 0; i + 1 < picks.Count; i++)
        {
            uint fromCell = picks[i].PickedCellId;
            uint toCell   = picks[i + 1].PickedCellId;
            if (Low(fromCell) < 0x100 || Low(toCell) < 0x100) continue; // indoor segments only

            var body   = GroundedBodyAt(picks[i].Position, fromCell);
            var result = engine.ResolveWithTransition(
                currentPos:     picks[i].Position,
                targetPos:      picks[i + 1].Position,
                cellId:         fromCell,
                sphereRadius:   SphereRadius,
                sphereHeight:   SphereHeight,
                stepUpHeight:   StepUpHeight,
                stepDownHeight: StepDownHeight,
                isOnGround:     true,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            bool ok = result.CellId == toCell;
            if (ok) match++;
            else failures.Add(System.FormattableString.Invariant(
                $"0x{Low(fromCell):X4}->0x{Low(toCell):X4}@({picks[i + 1].Position.X:F2},{picks[i + 1].Position.Y:F2}): acdream=0x{Low(result.CellId):X4}"));
            total++;
            _out.WriteLine(
                $"seg 0x{Low(fromCell):X4}->0x{Low(toCell):X4} " +
                $"pos=({picks[i].Position.X:F2},{picks[i].Position.Y:F2})->({picks[i + 1].Position.X:F2},{picks[i + 1].Position.Y:F2}) " +
                $"acdream=0x{Low(result.CellId):X4} restPos=({result.Position.X:F2},{result.Position.Y:F2},{result.Position.Z:F2}) " +
                $"{(ok ? "MATCH" : "DIVERGE")}");
        }
        _out.WriteLine($"=== production-path indoor crossings: {match}/{total} match retail ===");

        Assert.True(total > 0, "no indoor doorway segments found in the golden");
        Assert.True(failures.Count == 0,
            $"acdream's swept membership diverged on {failures.Count}/{total} indoor doorway " +
            $"crossings (retail truth = the aligned golden): {string.Join("; ", failures)}");
    }
}
