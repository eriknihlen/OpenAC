using System;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class CameraCornerSealReplayTests
{
    private readonly ITestOutputHelper _out;
    public CameraCornerSealReplayTests(ITestOutputHelper output) => _out = output;

    private const float ViewerSphereRadius = 0.3f;
    private const float PivotHeight = 1.5f;

    private static readonly (string Label, uint CellId, Vector3 Player, Vector3 Eye)[] Samples =
    {
        ("S1-render 0170", 0xA9B40170u, new Vector3(154.934845f, 16.451527f, 94.000000f),
            new Vector3(154.797028f, 19.320539f, 96.248833f)),
        ("S1-raw 0170",    0xA9B40170u, new Vector3(154.945374f, 16.451527f, 94.000000f),
            new Vector3(154.797028f, 19.320539f, 96.248833f)),
        ("S2 0171",        0xA9B40171u, new Vector3(155.164307f, 14.392493f, 94.000000f),
            new Vector3(154.804489f, 18.434128f, 96.248833f)),
        ("S3 0171",        0xA9B40171u, new Vector3(155.475723f, 11.463923f, 94.000000f),
            new Vector3(154.990143f, 16.249460f, 96.248833f)),
    };

    private static (PhysicsEngine, PhysicsDataCache,
        System.Collections.Generic.Dictionary<uint, AcDream.Core.World.Cells.EnvCell>)
        BuildBuildingEngine(DatCollection dats)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        var envCells = new System.Collections.Generic.Dictionary<uint, AcDream.Core.World.Cells.EnvCell>();
        for (uint low = 0x016Fu; low <= 0x0175u; low++)
        {
            uint id = ConformanceDats.HoltburgLandblock | low;
            envCells[id] = ConformanceDats.LoadEnvCell(dats, cache, id);
        }

        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(0xA9B40000u, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return (engine, cache, envCells);
    }

    private static ResolveResult SweepViewer(PhysicsEngine engine, Vector3 pivot, Vector3 desiredEye, uint cellId)
    {
        uint startCell = cellId;
        if ((cellId & 0xFFFFu) >= 0x0100u)
        {
            var (pivotCell, found) = engine.AdjustPosition(cellId, pivot);
            if (found) startCell = pivotCell;
        }

        Vector3 begin = pivot      - new Vector3(0f, 0f, ViewerSphereRadius);
        Vector3 end   = desiredEye - new Vector3(0f, 0f, ViewerSphereRadius);

        return engine.ResolveWithTransition(
            currentPos:     begin,
            targetPos:      end,
            cellId:         startCell,
            sphereRadius:   ViewerSphereRadius,
            sphereHeight:   0f,
            stepUpHeight:   0f,
            stepDownHeight: 0f,
            isOnGround:     false,
            body:           null,
            moverFlags:     ObjectInfoState.IsViewer | ObjectInfoState.PathClipped
                          | ObjectInfoState.FreeRotate | ObjectInfoState.PerfectClip,
            movingEntityId: 0);
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_DispatchTrace_LeakPath_vs_Controls()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var (engine, cache, envCells) = BuildBuildingEngine(dats);

        var s = Samples[0]; // S1-render 0170
        Vector3 pivot = s.Player + new Vector3(0f, 0f, PivotHeight);

        RunTraced(engine, "LEAK  viewer pivot->eye", () =>
            SweepViewer(engine, pivot, s.Eye, s.CellId));

        RunTraced(engine, "CTRL-H viewer horizontal +3Y", () =>
            SweepViewer(engine, pivot, pivot + new Vector3(0f, 3f, 0f), s.CellId));

        RunTraced(engine, "CTRL-P player grounded +3Y", () =>
            engine.ResolveWithTransition(
                currentPos:     s.Player,
                targetPos:      s.Player + new Vector3(0f, 3f, 0f),
                cellId:         s.CellId,
                sphereRadius:   0.4f,
                sphereHeight:   1.2f,
                stepUpHeight:   0.4f,
                stepDownHeight: 0.1f,
                isOnGround:     true,
                body:           null,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0));

        _out.WriteLine("=== containment along LEAK path (pivot -> eye, 11 steps) ===");
        for (int i = 0; i <= 10; i++)
        {
            Vector3 p = Vector3.Lerp(pivot, s.Eye, i / 10f);
            string inCells = "";
            foreach (var kv in envCells)
                if (kv.Value.PointInCell(p))
                    inCells += $" 0x{kv.Key & 0xFFFFu:X4}";
            if (inCells.Length == 0) inCells = " (none)";
            _out.WriteLine(System.FormattableString.Invariant(
                $"  t={i / 10f:F1} ({p.X:F2},{p.Y:F2},{p.Z:F2}) ->{inCells}"));
        }

        _out.WriteLine("=== full room map: origin + portals (cell-LOCAL polygon bounds) ===");
        foreach (uint low in new uint[] { 0x016Fu, 0x0170u, 0x0171u, 0x0172u, 0x0173u, 0x0174u, 0x0175u })
        {
            var cp = cache.GetCellStruct(ConformanceDats.HoltburgLandblock | low);
            if (cp is null) { _out.WriteLine($"  0x{low:X4}: no CellPhysics"); continue; }
            var o = cp.WorldTransform.Translation;
            _out.WriteLine(System.FormattableString.Invariant(
                $"  0x{low:X4} origin=({o.X:F2},{o.Y:F2},{o.Z:F2}) portals={cp.Portals.Count}"));
            foreach (var portal in cp.Portals)
            {
                string bounds = "(polygon missing)";
                if (cp.PortalPolygons is not null
                    && cp.PortalPolygons.TryGetValue(portal.PolygonId, out var poly))
                {
                    Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                    foreach (var v in poly.Vertices)
                    {
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }
                    bounds = System.FormattableString.Invariant(
                        $"x[{min.X:F2},{max.X:F2}] y[{min.Y:F2},{max.Y:F2}] z[{min.Z:F2},{max.Z:F2}]");
                }
                _out.WriteLine(System.FormattableString.Invariant(
                    $"    -> other=0x{portal.OtherCellId:X4} poly={portal.PolygonId} {bounds}"));
            }
        }

        _out.WriteLine("=== containment: corner-press eyes + pivots (player=0172 viewer=0171 frames) ===");
        var cornerPoints = new (string Label, Vector3 P)[]
        {
            ("eyeA (L23035)",  new Vector3(154.607880f, 11.154252f, 96.248787f)),
            ("eyeB (L81340)",  new Vector3(157.477249f,  7.912723f, 96.248863f)),
            ("eyeC (L87958)",  new Vector3(157.452667f,  7.914233f, 96.248856f)),
            ("pivotA",         new Vector3(157.976959f,  8.622595f, 95.500000f)),
            ("pivotB/C",       new Vector3(159.936676f,  7.701012f, 95.500000f)),
        };
        foreach (var (label, p) in cornerPoints)
        {
            string inCells = "";
            foreach (var kv in envCells)
                if (kv.Value.PointInCell(p))
                    inCells += $" 0x{kv.Key & 0xFFFFu:X4}";
            if (inCells.Length == 0) inCells = " (none)";
            _out.WriteLine(System.FormattableString.Invariant(
                $"  {label} ({p.X:F2},{p.Y:F2},{p.Z:F2}) ->{inCells}"));
        }
    }

    private void RunTraced(PhysicsEngine engine, string label, Func<ResolveResult> sweep)
    {
        var sw = new StringWriter();
        var prev = Console.Out;
        ResolveResult r;
        try
        {
            Console.SetOut(sw);
            PhysicsDiagnostics.ProbePushBackEnabled = true;
            r = sweep();
        }
        finally
        {
            PhysicsDiagnostics.ProbePushBackEnabled = false;
            Console.SetOut(prev);
        }

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _out.WriteLine(System.FormattableString.Invariant(
            $"=== {label}: end=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) cell=0x{r.CellId:X8} collNorm={r.CollisionNormalValid} ok={r.Ok} probeLines={lines.Length} ==="));
        for (int i = 0; i < lines.Length && i < 40; i++)
            _out.WriteLine("  " + lines[i].TrimEnd());
        if (lines.Length > 40)
            _out.WriteLine($"  ... +{lines.Length - 40} more");
    }

    [Fact]
    public void ViewerSweep_ThroughOpenings_PassesWithoutCollision()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var (engine, _, _) = BuildBuildingEngine(dats);

        var failures = new System.Collections.Generic.List<string>();
        foreach (var s in Samples)
        {
            Vector3 pivot = s.Player + new Vector3(0f, 0f, PivotHeight);
            float desiredBack = Vector3.Distance(pivot, s.Eye);

            var r = SweepViewer(engine, pivot, s.Eye, s.CellId);

            Vector3 eyeOut = r.Position + new Vector3(0f, 0f, ViewerSphereRadius);
            float eyeBack  = Vector3.Distance(pivot, eyeOut);
            float pulledIn = desiredBack - eyeBack;

            bool passedClean = !r.CollisionNormalValid && pulledIn < 0.10f && r.Ok;

            _out.WriteLine(System.FormattableString.Invariant(
                $"{s.Label}: ok={r.Ok} collNorm={r.CollisionNormalValid} desiredBack={desiredBack:F2} eyeBack={eyeBack:F2} pulledIn={pulledIn:F2} endCell=0x{r.CellId:X8} {(passedClean ? "CLEAN" : "OBSTRUCTED")}"));

            if (!passedClean)
                failures.Add(s.Label);
        }

        Assert.True(failures.Count == 0,
            "Viewer sweep through a verified-open doorway path was obstructed or cut short " +
            "(camera would wrongly pull in at openings) for: " + string.Join(", ", failures));
    }

    [Fact]
    public void HoltburgCottageExit_ViewerSweep_AdvancesFromAlcoveToOutdoor()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var graphCache = new PhysicsDataCache();
        var preparedCache = PhysicsDataCache.CreateProduction();
        for (uint low = 0x013Fu; low <= 0x0150u; low++)
        {
            uint cellId = ConformanceDats.HoltburgLandblock | low;
            ConformanceDats.LoadEnvCell(
                dats,
                graphCache,
                cellId);

            CellPhysics graphCell = Assert.IsType<CellPhysics>(
                graphCache.GetCellStruct(cellId));
            FlatCellCollisionAsset prepared =
                FlatCollisionAssetBuilder.FlattenCell(graphCell);
            var datCell = Assert.IsType<DatReaderWriter.DBObjs.EnvCell>(
                dats.Get<DatReaderWriter.DBObjs.EnvCell>(cellId));
            preparedCache.CacheCellStruct(
                cellId,
                datCell,
                graphCell.WorldTransform,
                prepared.Structure,
                prepared.Topology);
        }

        var graphEngine = new PhysicsEngine { DataCache = graphCache };
        var preparedEngine = new PhysicsEngine { DataCache = preparedCache };

        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < heightTable.Length; i++)
            heightTable[i] = -1000f;
        foreach (PhysicsEngine engine in new[] { graphEngine, preparedEngine })
        {
            engine.AddLandblock(
                ConformanceDats.HoltburgLandblock,
                new TerrainSurface(heights, heightTable),
                Array.Empty<CellSurface>(),
                Array.Empty<PortalPlane>(),
                0f,
                0f);
        }

        var player = new Vector3(132.70f, 16.80f, 94.00f);
        var pivot = player + new Vector3(0f, 0f, PivotHeight);
        var eye = new Vector3(131.30f, 19.97f, 96.25f);

        var graphPivot = graphEngine.AdjustPosition(0xA9B40150u, pivot);
        var preparedPivot = preparedEngine.AdjustPosition(0xA9B40150u, pivot);
        Assert.True(graphPivot.found);
        Assert.True(preparedPivot.found);
        Assert.Equal(0xA9B40150u, graphPivot.cellId);
        Assert.Equal(graphPivot.cellId, preparedPivot.cellId);

        ResolveResult graphResult = SweepViewer(
            graphEngine,
            pivot,
            eye,
            0xA9B40150u);
        ResolveResult preparedResult = SweepViewer(
            preparedEngine,
            pivot,
            eye,
            0xA9B40150u);

        _out.WriteLine(FormattableString.Invariant(
            $"graph=0x{graphResult.CellId:X8} prepared=0x{preparedResult.CellId:X8} graphPos=({graphResult.Position.X:F3},{graphResult.Position.Y:F3},{graphResult.Position.Z:F3}) preparedPos=({preparedResult.Position.X:F3},{preparedResult.Position.Y:F3},{preparedResult.Position.Z:F3})"));

        Assert.Equal(0xA9B40029u, graphResult.CellId);
        Assert.Equal(graphResult.CellId, preparedResult.CellId);
        Assert.Equal(graphResult.Position, preparedResult.Position);
    }
}
