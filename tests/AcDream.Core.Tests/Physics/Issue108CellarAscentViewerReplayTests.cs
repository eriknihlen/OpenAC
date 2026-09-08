using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class Issue108CellarAscentViewerReplayTests
{
    private readonly ITestOutputHelper _out;
    public Issue108CellarAscentViewerReplayTests(ITestOutputHelper output) => _out = output;

    private const float ViewerSphereRadius = 0.3f;
    private const float PivotHeight = 1.5f;
    private const float FootRadius = 0.48f;         // player foot sphere
    private const float BoomDistance = 2.61f;
    private const float BoomPitch = 0.291f;
    private const float GradeZ = 94.0f;

    private const uint Lb = 0xA9B40000u;
    private const uint CellarRoom = Lb | 0x0174u;   // floor z≈90.0
    private const uint MainFloor = Lb | 0x0171u;    // z=94.0

    // ── fixture ─────────────────────────────────────────────────────────

    private static (PhysicsEngine engine, PhysicsDataCache cache,
        Dictionary<uint, AcDream.Core.World.Cells.EnvCell> envCells)
        BuildEngine(DatCollection dats)
    {
        var cache = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        var envCells = new Dictionary<uint, AcDream.Core.World.Cells.EnvCell>();

        for (uint low = 0x0100u; low <= 0x01FFu; low++)
        {
            try { envCells[Lb | low] = ConformanceDats.LoadEnvCell(dats, cache, Lb | low); }
            catch { }
        }

        var lbInfo = dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>(Lb | 0xFFFEu);
        Assert.NotNull(lbInfo);
        foreach (var building in lbInfo!.Buildings)
        {
            if (building.Portals.Count == 0) continue;
            var portals = new List<BldPortalInfo>(building.Portals.Count);
            foreach (var bp in building.Portals)
                portals.Add(new BldPortalInfo(
                    otherCellId: Lb | (uint)bp.OtherCellId,
                    otherPortalId: unchecked((short)bp.OtherPortalId),
                    flags: (ushort)bp.Flags));
            var transform =
                Matrix4x4.CreateFromQuaternion(building.Frame.Orientation) *
                Matrix4x4.CreateTranslation(building.Frame.Origin);
            int gridX = (int)(building.Frame.Origin.X / 24f);
            int gridY = (int)(building.Frame.Origin.Y / 24f);
            uint landcellLow = (uint)(gridX * 8 + gridY + 1);
            cache.CacheBuilding(Lb | landcellLow, portals, transform);
        }

        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(Lb, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);

        return (engine, cache, envCells);
    }


    private enum ViewerBranch { Sweep, AdjustFallback, NullFallback }

    private sealed record ViewerResolve(
        Vector3 Eye, uint ViewerCellId, ViewerBranch Branch,
        uint StartCell, bool PivotAdjustFound, ResolveResult Sweep);

    private static ViewerResolve ResolveViewer(
        PhysicsEngine engine, Vector3 pivot, Vector3 desiredEye, uint cellId, Vector3 playerPos)
    {
        if (cellId == 0u)
            return new ViewerResolve(playerPos, 0u, ViewerBranch.NullFallback, 0u, false, default);

        uint startCell = cellId;
        bool pivotFound = false;
        if ((cellId & 0xFFFFu) >= 0x0100u)
        {
            var (pivotCell, found) = engine.AdjustPosition(cellId, pivot);
            pivotFound = found;
            if (found) startCell = pivotCell;
        }

        Vector3 begin = pivot - new Vector3(0f, 0f, ViewerSphereRadius);
        Vector3 end = desiredEye - new Vector3(0f, 0f, ViewerSphereRadius);

        var r = engine.ResolveWithTransition(
            currentPos: begin,
            targetPos: end,
            cellId: startCell,
            sphereRadius: ViewerSphereRadius,
            sphereHeight: 0f,
            stepUpHeight: 0f,
            stepDownHeight: 0f,
            isOnGround: false,
            body: null,
            moverFlags: ObjectInfoState.IsViewer | ObjectInfoState.PathClipped
                      | ObjectInfoState.FreeRotate | ObjectInfoState.PerfectClip,
            movingEntityId: 0);

        Vector3 eye = r.Position + new Vector3(0f, 0f, ViewerSphereRadius);
        if (r.Ok)
            return new ViewerResolve(eye, r.CellId, ViewerBranch.Sweep, startCell, pivotFound, r);

        var (eyeCell, eyeFound) = engine.AdjustPosition(cellId, desiredEye);
        if (eyeFound)
            return new ViewerResolve(desiredEye, eyeCell, ViewerBranch.AdjustFallback, startCell, pivotFound, r);

        return new ViewerResolve(playerPos, 0u, ViewerBranch.NullFallback, startCell, pivotFound, r);
    }

    // ── the ascent ──────────────────────────────────────────────────────

    private static float FeetZ(float y)
    {
        if (y < 5.73f) return 90.0f;
        if (y < 9.30f) return MathF.Min(90.25f + 0.836f * (y - 5.73f), 93.25f);
        if (y < 10.40f) return 93.25f + (y - 9.30f) * (0.75f / 1.10f);
        return 94.0f;
    }

    private sealed record Step(
        int Index, Vector3 Feet, uint PlayerCell,
        ViewerResolve Viewer, uint EyeContainedIn, bool EyeBelowGrade)
    {
        public bool ViewerOutdoorOrNull =>
            Viewer.ViewerCellId == 0u || (Viewer.ViewerCellId & 0xFFFFu) < 0x0100u;
    }

    private List<Step>? RunAscent(float boomDistance, float pathLagMeters)
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); return null; }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var (engine, _, envCells) = BuildEngine(dats);

        const float yStart = 5.2f, yEnd = 16.0f;
        const float stepLen = 0.02f;                 // 2 cm/frame ≈ 1.2 m/s at 60 Hz
        var fwd = new Vector3(0f, 1f, 0f);           // facing up the stairs / at the exit door
        float cosP = MathF.Cos(BoomPitch), sinP = MathF.Sin(BoomPitch);

        static float FeetX(float y) =>
            y <= 10.4f ? 153.9f
            : y >= 14.0f ? 155.0f
            : 153.9f + (y - 10.4f) / (14.0f - 10.4f) * (155.0f - 153.9f);

        var steps = new List<Step>();
        uint playerCell = CellarRoom;
        int count = (int)MathF.Round((yEnd - yStart) / stepLen);

        for (int i = 0; i <= count; i++)
        {
            float y = yStart + i * stepLen;
            var feet = new Vector3(FeetX(y), y, FeetZ(y));

            playerCell = CellTransit.FindCellList(
                engine.DataCache!, feet + new Vector3(0f, 0f, FootRadius), FootRadius, playerCell);

            float yBoom = MathF.Max(yStart, y - pathLagMeters);
            var boomFeet = new Vector3(FeetX(yBoom), yBoom, FeetZ(yBoom));
            var pivot = feet + new Vector3(0f, 0f, PivotHeight);
            var boomPivot = boomFeet + new Vector3(0f, 0f, PivotHeight);
            var desiredEye = boomPivot - fwd * (boomDistance * cosP)
                           + new Vector3(0f, 0f, boomDistance * sinP);

            var viewer = ResolveViewer(engine, pivot, desiredEye, playerCell, feet);

            uint containedIn = 0u;
            foreach (var (id, env) in envCells)
                if (env.PointInCell(viewer.Eye)) { containedIn = id; break; }

            steps.Add(new Step(i, feet, playerCell, viewer,
                containedIn, viewer.Eye.Z < GradeZ - 0.05f));
        }

        return steps;
    }

    private void DumpStep(Step s)
    {
        var v = s.Viewer;
        string line = FormattableString.Invariant(
            $"step={s.Index,3} feet=({s.Feet.X:F2},{s.Feet.Y:F2},{s.Feet.Z:F2}) pCell=0x{s.PlayerCell & 0xFFFFu:X4} start=0x{v.StartCell & 0xFFFFu:X4}{(v.PivotAdjustFound ? "" : "!")} branch={v.Branch} ok={v.Sweep.Ok} eye=({v.Eye.X:F2},{v.Eye.Y:F2},{v.Eye.Z:F2}) viewer=0x{v.ViewerCellId & 0xFFFFu:X4} eyeIn=0x{s.EyeContainedIn & 0xFFFFu:X4} belowGrade={(s.EyeBelowGrade ? "Y" : "n")}");
        if (s.EyeBelowGrade && s.ViewerOutdoorOrNull) line += "  << GRASS-WINDOW";
        _out.WriteLine(line);
    }

    // ── diagnostics + pins ──────────────────────────────────────────────

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_CellarAscent_PerStepTable()
    {
        var steps = RunAscent(BoomDistance, pathLagMeters: 0f);
        if (steps is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        uint lastPlayer = 0; uint lastViewer = 0xFFFFFFFFu; var lastBranch = (ViewerBranch)(-1);
        int suspicious = 0;
        foreach (var s in steps)
        {
            bool grass = s.EyeBelowGrade && s.ViewerOutdoorOrNull;
            if (grass) suspicious++;
            if (s.PlayerCell != lastPlayer || s.Viewer.ViewerCellId != lastViewer
                || s.Viewer.Branch != lastBranch || grass || s.Index % 50 == 0)
                DumpStep(s);
            lastPlayer = s.PlayerCell; lastViewer = s.Viewer.ViewerCellId; lastBranch = s.Viewer.Branch;
        }
        _out.WriteLine(FormattableString.Invariant(
            $"--- {suspicious}/{steps.Count} steps in the grass window (viewer outdoor/null while eye below grade) ---"));
    }

    /// <summary>Boom-distance + damping-lag sweep: how wide is the window across poses?</summary>
    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_CellarAscent_PoseSweep()
    {
        foreach (float dist in new[] { 2.61f, 5.0f })
            foreach (float lag in new[] { 0f, 0.30f })
            {
                var steps = RunAscent(dist, lag);
                if (steps is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
                int grass = steps.FindAll(s => s.EyeBelowGrade && s.ViewerOutdoorOrNull).Count;
                int okFalse = steps.FindAll(s => !s.Viewer.Sweep.Ok).Count;
                int fb = steps.FindAll(s => s.Viewer.Branch != ViewerBranch.Sweep).Count;
                _out.WriteLine(FormattableString.Invariant(
                    $"dist={dist:F2} lag={lag:F2}: grassWindow={grass}/{steps.Count} sweepOkFalse={okFalse} fallbackBranch={fb}"));
            }
    }

    [Fact]
    public void CellarAscent_ViewerStaysInterior_WhileEyeBelowGrade()
    {
        var steps = RunAscent(BoomDistance, pathLagMeters: 0f);
        if (steps is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var failures = steps.FindAll(s => s.EyeBelowGrade && s.ViewerOutdoorOrNull);
        if (failures.Count > 0)
        {
            _out.WriteLine($"--- {failures.Count} grass-window steps ---");
            foreach (var s in failures) DumpStep(s);
        }
        Assert.True(failures.Count == 0,
            $"{failures.Count}/{steps.Count} ascent steps resolve an outdoor/null viewer cell while the eye " +
            "is below grade — the #108 grass window (see output for the branch attribution)");
    }
}
