using System;
using System.IO;
using System.Numerics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class CorridorSeamReplayTests
{
    private readonly ITestOutputHelper _out;
    public CorridorSeamReplayTests(ITestOutputHelper output) => _out = output;

    private const uint SeamCellWest = 0x8A02016Eu;
    private const uint SeamCellEast = 0x8A02017Au;

    private static string? FindDatDir()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(datDir) ? datDir : null;
    }

    private static PhysicsEngine BuildCorridorEngine(DatCollection dats)
    {
        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();

        var toLoad = new System.Collections.Generic.HashSet<uint> { SeamCellWest, SeamCellEast };
        foreach (var seed in new[] { SeamCellWest, SeamCellEast })
        {
            var seedCell = dats.Get<EnvCell>(seed);
            Assert.NotNull(seedCell);
            foreach (var p in seedCell!.CellPortals)
                toLoad.Add(0x8A020000u | p.OtherCellId);
        }

        for (int ring = 0; ring < 3; ring++)
        {
            foreach (var known in new System.Collections.Generic.List<uint>(toLoad))
            {
                var cell = dats.Get<EnvCell>(known);
                if (cell is null) continue;
                foreach (var p in cell.CellPortals)
                    toLoad.Add(0x8A020000u | p.OtherCellId);
            }
        }

        foreach (var cellId in toLoad)
        {
            var envCell = dats.Get<EnvCell>(cellId);
            if (envCell is null) continue;
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell.EnvironmentId);
            if (environment is null) continue;
            if (!environment.Cells.TryGetValue(envCell.CellStructure, out var cs)) continue;

            var rot = new Quaternion(
                envCell.Position.Orientation.X, envCell.Position.Orientation.Y,
                envCell.Position.Orientation.Z, envCell.Position.Orientation.W);
            var world = Matrix4x4.CreateFromQuaternion(rot)
                      * Matrix4x4.CreateTranslation(
                            envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);

            engine.DataCache.CacheCellStruct(cellId, envCell, cs!, world);
        }

        return engine;
    }

    private static PhysicsBody GroundedBody()
    {
        var body = new PhysicsBody();
        body.ContactPlaneValid = true;
        body.ContactPlane      = new Plane(Vector3.UnitZ, 6f);
        body.TransientState   |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
        body.WalkablePolygonValid = true;
        body.WalkablePlane        = new Plane(Vector3.UnitZ, 6f);
        body.WalkableUp           = Vector3.UnitZ;
        body.WalkableVertices     = new[]
        {
            new Vector3(75f, -41.67f, -6f),
            new Vector3(85f, -41.67f, -6f),
            new Vector3(85f, -38.33f, -6f),
            new Vector3(75f, -38.33f, -6f),
        };
        return body;
    }

    private ResolveResult Resolve(PhysicsEngine engine, PhysicsBody body,
        Vector3 from, Vector3 to, uint cellId)
        => engine.ResolveWithTransition(
            currentPos:     from,
            targetPos:      to,
            cellId:         cellId,
            sphereRadius:   0.48f,   // human player, PlayerMovementController:885
            sphereHeight:   1.2f,    // human player, PlayerMovementController:886
            stepUpHeight:   0.4f,    // PlayerMovementController defaults
            stepDownHeight: 0.4f,
            isOnGround:     true,
            body:           body,
            moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);

    [Fact]
    public void SeamShake_WestBoundary_SnapshotExact_Advances()
    {
        var datDir = FindDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: dat directory not found");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var engine = BuildCorridorEngine(dats);

        var body = new PhysicsBody();
        body.ContactPlaneValid   = true;
        body.ContactPlane        = new Plane(Vector3.UnitZ, 6f);
        body.ContactPlaneCellId  = SeamCellWest;
        body.TransientState     |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;
        body.WalkablePolygonValid = true;
        body.WalkablePlane        = new Plane(Vector3.UnitZ, 6f);
        body.WalkableUp           = Vector3.UnitZ;
        body.WalkableVertices     = new[]
        {
            new Vector3(75f,       -38.33333f, -6f),
            new Vector3(75f,       -41.66667f, -6f),
            new Vector3(78.33333f, -41.66667f, -6f),
            new Vector3(78.33333f, -38.33333f, -6f),
        };

        var from = new Vector3(75.28674f, -40.03537f, -6f);
        var to   = new Vector3(74.6854f,  -39.988018f, -6f);

        // Emit the same step-level probes the live session logged so the
        // offline trace can be line-diffed against launch-137-seam-probes.log
        // — the first divergent line names the state the replay is missing.
        var probeBuffer = new System.IO.StringWriter();
        var prevOut = Console.Out;
        ResolveResult r1;
        try
        {
            Console.SetOut(probeBuffer);
            PhysicsDiagnostics.ProbeStepWalkEnabled  = true;
            PhysicsDiagnostics.ProbePushBackEnabled  = true;
            PhysicsDiagnostics.ProbeIndoorBspEnabled = true;

            r1 = engine.ResolveWithTransition(
                currentPos:     from,
                targetPos:      to,
                cellId:         SeamCellWest,
                sphereRadius:   0.48f,
                sphereHeight:   1.2f,
                stepUpHeight:   0.6f,
                stepDownHeight: 1.5f,
                isOnGround:     true,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);
        }
        finally
        {
            PhysicsDiagnostics.ProbeStepWalkEnabled  = false;
            PhysicsDiagnostics.ProbePushBackEnabled  = false;
            PhysicsDiagnostics.ProbeIndoorBspEnabled = false;
            Console.SetOut(prevOut);
        }
        _out.WriteLine(probeBuffer.ToString());

        _out.WriteLine($"r1: ok={r1.Ok} out=({r1.Position.X:F3},{r1.Position.Y:F3},{r1.Position.Z:F3}) " +
                       $"cell=0x{r1.CellId:X8} hit={r1.CollisionNormalValid} " +
                       $"n=({r1.CollisionNormal.X:F2},{r1.CollisionNormal.Y:F2},{r1.CollisionNormal.Z:F2}) " +
                       $"bodySliding={body.TransientState.HasFlag(TransientStateFlags.Sliding)}");

        Assert.True(r1.Position.X < from.X - 0.3f,
            $"The westward boundary crossing onto the ramp must advance " +
            $"({from.X:F3} → {r1.Position.X:F3}, target {to.X:F3}); zero " +
            $"advance with the reversed-movement normal = the seam shake.");
    }

    [Fact]
    public void WindowOpening_HeadCannotFit_EntryBlocked()
    {
        var datDir = FindDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: dat directory not found");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var engine = BuildCorridorEngine(dats);

        var body = new PhysicsBody();
        body.ContactPlaneValid  = true;
        body.ContactPlane       = new Plane(Vector3.UnitZ, 5.112f);   // ramp-top level
        body.ContactPlaneCellId = 0x8A020179u;
        body.TransientState    |= TransientStateFlags.Contact | TransientStateFlags.OnWalkable;

        var pos  = new Vector3(88.60f, -41.10f, -5.05f);
        uint cell = 0x8A020179u;
        ResolveResult r = default;
        bool probeFrames = Env.GetEnvironmentVariable("ACDREAM_TEST_WINDOW_PROBE") == "1";
        for (int i = 0; i < 22; i++)
        {
            var dir  = Vector3.Normalize(new Vector3(90.209f, -41.809f, 0f) - new Vector3(pos.X, pos.Y, 0f));
            var step = new Vector3(dir.X, dir.Y, 0f) * 0.13f;

            var probeBuffer = new System.IO.StringWriter();
            var prevOut = Console.Out;
            try
            {
                if (probeFrames && i >= 9)
                {
                    Console.SetOut(probeBuffer);
                    PhysicsDiagnostics.ProbeStepWalkEnabled  = true;
                    PhysicsDiagnostics.ProbeIndoorBspEnabled = true;
                }
                r = engine.ResolveWithTransition(
                    currentPos:     pos,
                    targetPos:      pos + step,
                    cellId:         cell,
                    sphereRadius:   0.48f,
                    sphereHeight:   1.835f,
                    stepUpHeight:   0.6f,
                    stepDownHeight: 1.5f,
                    isOnGround:     true,
                    body:           body,
                    moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);
            }
            finally
            {
                if (probeFrames && i >= 9)
                {
                    PhysicsDiagnostics.ProbeStepWalkEnabled  = false;
                    PhysicsDiagnostics.ProbeIndoorBspEnabled = false;
                    Console.SetOut(prevOut);
                }
            }
            if (probeFrames && i >= 9 && i <= 10)
                _out.WriteLine(probeBuffer.ToString());
            _out.WriteLine($"r{i}: ok={r.Ok} out=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) " +
                           $"cell=0x{r.CellId:X8} hit={r.CollisionNormalValid} " +
                           $"n=({r.CollisionNormal.X:F2},{r.CollisionNormal.Y:F2},{r.CollisionNormal.Z:F2})");
            pos  = r.Position;
            cell = r.CellId;

            Assert.NotEqual(0x8A02017Eu, r.CellId);
            Assert.True(r.Position.Y > -41.6f,
                $"A 1.68 m character must not enter the 1.3 m-tall opening " +
                $"(wall plane y=−41.67); frame {i} got Y={r.Position.Y:F3} " +
                $"cell=0x{r.CellId:X8} (live bug: ended at −41.774 inside " +
                $"0x8A02017E, head through the roof).");
        }
    }

    [Fact]
    public void WindowAlcove_RaisedPlacement_HeadInLintelSolid_Collides()
    {
        var datDir = FindDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: dat directory not found");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var engine = BuildCorridorEngine(dats);

        var cell = engine.DataCache!.GetCellStruct(0x8A020179u);
        Assert.NotNull(cell);
        Assert.NotNull(cell!.BSP?.Root);

        var footWorld = new Vector3(89.683f, -41.247f, -4.539f);
        var headWorld = new Vector3(89.683f, -41.247f, -3.339f);

        var footLocal = Vector3.Transform(footWorld, cell.InverseWorldTransform);
        var headLocal = Vector3.Transform(headWorld, cell.InverseWorldTransform);

        var t = new Transition();
        t.SpherePath.InitPath(
            new Vector3(89.683f, -41.247f, -5.019f),
            new Vector3(89.683f, -41.247f, -5.019f),
            0x8A020179u, 0.48f, 1.2f);
        t.SpherePath.InsertType = InsertType.Placement;

        Matrix4x4.Decompose(cell.WorldTransform, out _, out var cellRot, out var cellOrigin);

        var result = BSPQuery.FindCollisions(
            cell.BSP!.Root,
            cell.Resolved,
            t,
            new DatReaderWriter.Types.Sphere { Origin = footLocal, Radius = 0.48f },
            new DatReaderWriter.Types.Sphere { Origin = headLocal, Radius = 0.48f },
            footLocal,
            Vector3.UnitZ,
            1.0f,
            cellRot,
            engine,
            worldOrigin: cellOrigin);

        _out.WriteLine($"placement result={result} footLocal=({footLocal.X:F3},{footLocal.Y:F3},{footLocal.Z:F3}) " +
                       $"headLocal=({headLocal.X:F3},{headLocal.Y:F3},{headLocal.Z:F3})");

        Assert.Equal(TransitionState.Collided, result);
    }

    [Fact]
    public void SeamCrossing_FromDeepStraddleStart_Advances()
    {
        var datDir = FindDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: dat directory not found");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var engine = BuildCorridorEngine(dats);
        var body   = GroundedBody();

        var from = new Vector3(84.851f, -39.764f, -6.000f);
        var to   = new Vector3(85.453f, -39.782f, -6.000f);

        var r1 = Resolve(engine, body, from, to, SeamCellWest);
        _out.WriteLine($"r1: ok={r1.Ok} out=({r1.Position.X:F3},{r1.Position.Y:F3},{r1.Position.Z:F3}) " +
                       $"cell=0x{r1.CellId:X8} hit={r1.CollisionNormalValid} " +
                       $"n=({r1.CollisionNormal.X:F2},{r1.CollisionNormal.Y:F2},{r1.CollisionNormal.Z:F2}) " +
                       $"bodySliding={body.TransientState.HasFlag(TransientStateFlags.Sliding)} " +
                       $"bodyCpValid={body.ContactPlaneValid}");

        Assert.True(r1.Position.X > from.X + 0.2f,
            $"The straddling-start seam crossing must advance " +
            $"({from.X:F3} → {r1.Position.X:F3}); zero advance with a " +
            $"reversed-movement normal = the 2026-07-06 seam shake.");
    }

    [Fact]
    public void SeamCrossing_DoesNotPersistSyntheticSlidingNormal_AndRunContinues()
    {
        var datDir = FindDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: dat directory not found");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var engine = BuildCorridorEngine(dats);
        var body   = GroundedBody();

        var from = new Vector3(84.638f, -39.758f, -6.000f);
        var to   = new Vector3(85.253f, -39.776f, -6.000f);

        var r1 = Resolve(engine, body, from, to, SeamCellWest);
        _out.WriteLine($"r1: ok={r1.Ok} out=({r1.Position.X:F3},{r1.Position.Y:F3},{r1.Position.Z:F3}) " +
                       $"cell=0x{r1.CellId:X8} hit={r1.CollisionNormalValid} " +
                       $"n=({r1.CollisionNormal.X:F2},{r1.CollisionNormal.Y:F2},{r1.CollisionNormal.Z:F2}) " +
                       $"bodySliding={body.TransientState.HasFlag(TransientStateFlags.Sliding)} " +
                       $"slidingN=({body.SlidingNormal.X:F2},{body.SlidingNormal.Y:F2},{body.SlidingNormal.Z:F2})");

        Assert.False(body.TransientState.HasFlag(TransientStateFlags.Sliding),
            "Crossing the open corridor seam must not persist a sliding " +
            "normal — the live wedge's entry state.");

        // ── Keep running +X (the live session's held-W frames) ──────────
        var pos  = r1.Position;
        var cell = r1.CellId;
        for (int i = 0; i < 6; i++)
        {
            var step = new Vector3(0.13f, -0.004f, 0f);   // ~run speed per tick, same heading
            var r = Resolve(engine, body, pos, pos + step, cell);
            _out.WriteLine($"r{i + 2}: ok={r.Ok} out=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) " +
                           $"cell=0x{r.CellId:X8} hit={r.CollisionNormalValid} " +
                           $"bodySliding={body.TransientState.HasFlag(TransientStateFlags.Sliding)}");
            Assert.True(r.Position.X > pos.X + 0.05f,
                $"Forward run must keep advancing through the open corridor " +
                $"(frame {i + 2}: {pos.X:F3} → {r.Position.X:F3}) — zero advance " +
                $"= the absorbing wedge.");
            pos  = r.Position;
            cell = r.CellId;
        }
    }
}
