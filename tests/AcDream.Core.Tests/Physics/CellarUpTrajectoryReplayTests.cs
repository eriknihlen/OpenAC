using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.App.Input;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellarUpTrajectoryReplayTests : IDisposable
{
    public CellarUpTrajectoryReplayTests()
    {
        PhysicsResolveCapture.ResetForTest();
        PhysicsDiagnostics.ResetForTest();
    }

    public void Dispose()
    {
        PhysicsResolveCapture.ResetForTest();
        PhysicsDiagnostics.ResetForTest();
    }

    private const uint CellarId         = 0xA9B40147u;
    private const uint CottageNeighborA = 0xA9B40143u;
    private const uint CottageNeighborB = 0xA9B40146u;

    private const float CellarFloorZ  = 90.95f;
    private const float CottageFloorZ = 94.00f;

    private const float SphereRadius  = 0.48f;
    private const float SphereHeight  = 1.20f;
    private const float StepUpHeight   = 0.60f;
    private const float StepDownHeight = 0.04f;

    private static readonly Vector3 InitialSphereWorld =
        new(141.5f, 9.5f, CellarFloorZ + SphereRadius);

    private static readonly Vector3 PerTickOffset =
        new(0f, -0.10f, 0f);

    private const int SimulationTicks = 200;

    // ───────────────────────────────────────────────────────────────
    // Tests
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void IndoorCellarFloor_AtRestZeroOffset_BodyPositionBitStable()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();

        var body  = BuildInitialBody();
        var rest  = body.Position;
        uint cell = CellarId;
        bool grounded = true;

        var log = new List<string>();
        float maxDrift = 0f;
        for (int tick = 1; tick <= 200; tick++)
        {
            var result = engine.ResolveWithTransition(
                currentPos:     body.Position,
                targetPos:      body.Position,
                cellId:         cell,
                sphereRadius:   SphereRadius,
                sphereHeight:   SphereHeight,
                stepUpHeight:   StepUpHeight,
                stepDownHeight: StepDownHeight,
                isOnGround:     grounded,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            cell          = result.CellId;
            grounded      = result.IsOnGround;

            float drift = (body.Position - rest).Length();
            maxDrift = MathF.Max(maxDrift, drift);

            if (tick <= 6 || drift > 0f)
            {
                log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "tick{0,3}: pos=({1:F7},{2:F7},{3:F7}) drift={4:F3}µm grounded={5} " +
                    "walkable={6} cpV={7} ts=0x{8:X}",
                    tick, body.Position.X, body.Position.Y, body.Position.Z,
                    drift * 1e6f, grounded, body.WalkablePolygonValid,
                    body.ContactPlaneValid, (uint)body.TransientState));
            }
        }

        Assert.True(maxDrift == 0f,
            $"cellar-floor rest drifted {maxDrift * 1e6f:F3} µm (expected byte-identical):\n  "
            + string.Join("\n  ", log.Take(24)));
    }

    [Fact]
    public void IndoorCell_FullController_AtRestNoInput_RenderPositionBitStable()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();
        var controller  = new PlayerMovementController(engine);
        controller.SeedPlacementForTest(InitialSphereWorld, CellarId, InitialSphereWorld);

        var settled    = controller.Update(1f / 60f, new MovementInput());
        var basePos    = settled.Position;
        var baseRender = settled.RenderPosition;

        var log = new List<string>();
        float maxPos = 0f, maxRender = 0f;
        for (int i = 1; i <= 600; i++)
        {
            var r = controller.Update(1f / 60f, new MovementInput());
            float dp = (r.Position - basePos).Length();
            float dr = (r.RenderPosition - baseRender).Length();
            maxPos    = MathF.Max(maxPos, dp);
            maxRender = MathF.Max(maxRender, dr);
            if (i <= 4 || dp > 0f || dr > 0f)
            {
                log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "f{0,3}: pos=({1:F7},{2:F7},{3:F7}) render=({4:F7},{5:F7},{6:F7}) " +
                    "grounded={7} cell=0x{8:X8}",
                    i, r.Position.X, r.Position.Y, r.Position.Z,
                    r.RenderPosition.X, r.RenderPosition.Y, r.RenderPosition.Z,
                    r.IsOnGround, r.CellId));
            }
        }

        Assert.True(maxPos == 0f && maxRender == 0f,
            $"indoor controller rest drifted: pos={maxPos * 1e6f:F3} µm, "
            + $"render={maxRender * 1e6f:F3} µm (expected byte-identical):\n  "
            + string.Join("\n  ", log.Take(24)));
    }

    [Fact]
    public void Harness_CompilesAndRunsSimulation()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();
        var body = BuildInitialBody();
        var trajectory = SimulateTicks(engine, body, CellarId, SimulationTicks);

        Assert.Equal(SimulationTicks + 1, trajectory.Count);
        Assert.Equal(0, trajectory[0].Tick);
        Assert.Equal(SimulationTicks, trajectory[^1].Tick);
    }

    /// <summary>
    /// Diagnostic dump: print the first 10 trajectory points + the
    /// engine's resolve-probe decisions. Useful when investigating
    /// what the harness is actually doing.
    /// </summary>
    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Harness_DiagnosticDump_FirstTenTicks()
    {
        PhysicsDiagnostics.ProbeResolveEnabled    = true;
        PhysicsDiagnostics.ProbeStepWalkEnabled   = true;
        PhysicsDiagnostics.ProbeIndoorBspEnabled  = true;
        PhysicsDiagnostics.ProbePolyDumpEnabled   = true;
        try
        {
            var (engine, _) = BuildEngineWithCellarFixtures();
            var body = BuildInitialBody();
            var trajectory = SimulateTicks(engine, body, CellarId, 2);

            var msg = "Trajectory (2 ticks):\n  " +
                string.Join("\n  ", trajectory.Select(p =>
                    $"tick={p.Tick} pos=({p.Position.X:F4},{p.Position.Y:F4},{p.Position.Z:F4}) " +
                    $"cell=0x{p.CellId:X8} onGround={p.IsOnGround} cpValid={p.CpValid}"));
            Console.WriteLine(msg);
        }
        finally
        {
            PhysicsDiagnostics.ProbeResolveEnabled    = false;
            PhysicsDiagnostics.ProbeStepWalkEnabled   = false;
            PhysicsDiagnostics.ProbeIndoorBspEnabled  = false;
            PhysicsDiagnostics.ProbePolyDumpEnabled   = false;
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Harness_DiagnosticDump_NoBodySeed()
    {
        PhysicsDiagnostics.ProbeResolveEnabled = true;
        try
        {
            var (engine, _) = BuildEngineWithCellarFixtures();

            uint cellId      = CellarId;
            bool isOnGround  = true;
            Vector3 pos      = InitialSphereWorld;

            var trajectory = new List<TrajectoryPoint>
            {
                new(0, pos, cellId, isOnGround, false),
            };

            for (int tick = 1; tick <= 10; tick++)
            {
                Vector3 target = pos + PerTickOffset;
                var result = engine.ResolveWithTransition(
                    pos, target, cellId,
                    SphereRadius, SphereHeight,
                    StepUpHeight, StepDownHeight,
                    isOnGround,
                    body:           null,            // ← no body, no CP seed
                    moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                    movingEntityId: 0);

                pos = result.Position;
                cellId = result.CellId;
                isOnGround = result.IsOnGround;
                trajectory.Add(new(tick, pos, cellId, isOnGround, false));
            }

            var msg = "No-body trajectory (10 ticks):\n  " +
                string.Join("\n  ", trajectory.Select(p =>
                    $"tick={p.Tick} pos=({p.Position.X:F4},{p.Position.Y:F4},{p.Position.Z:F4}) " +
                    $"onGround={p.IsOnGround}"));
            Console.WriteLine(msg);
        }
        finally
        {
            PhysicsDiagnostics.ProbeResolveEnabled = false;
        }
    }

    [Fact]
    public void Harness_Finding_SphereGoesAirborneAtTick1()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();
        var body = BuildInitialBody();
        var trajectory = SimulateTicks(engine, body, CellarId, 3);

        Assert.True(trajectory[0].IsOnGround,
            "Tick 0 is the seeded starting state and must report grounded.");
        Assert.False(trajectory[1].IsOnGround,
            "Open finding: at tick 1 the engine reports the sphere is NOT " +
            "grounded, even though it started seeded with ContactPlane + " +
            "WalkablePolygon on the cellar floor and the cell has a " +
            "synthetic BSP wrapping every polygon. Hit normal is (0,1,0) — " +
            "doesn't match any registered geometry. Source of (0,1,0) " +
            "inside TransitionalInsert is not yet isolated. See the class " +
            "doc for the exclusion list and next investigation move.");
    }

    [Fact]
    public void Harness_SimulationRunsInUnder500ms()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();
        var body = BuildInitialBody();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = SimulateTicks(engine, body, CellarId, SimulationTicks);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"200-tick simulation should complete in under 500 ms. " +
            $"Took: {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Capture_WritesJsonLinesRecordsWhenIsPlayerAndEnabled()
    {
        string capturePath = Path.Combine(
            Path.GetTempPath(),
            $"acdream_capture_{Guid.NewGuid():N}.jsonl");

        try
        {
            PhysicsResolveCapture.CapturePath = capturePath;
            PhysicsResolveCapture.ResetTickCounter();

            var (engine, _) = BuildEngineWithCellarFixtures();
            var body = BuildInitialBody();
            _ = SimulateTicks(engine, body, CellarId, 3);

            PhysicsResolveCapture.Close();

            Assert.True(File.Exists(capturePath), "Capture file should exist.");
            var lines = File.ReadAllLines(capturePath);
            Assert.Equal(3, lines.Length);

            var records = lines
                .Select(static l => System.Text.Json.JsonSerializer.Deserialize<ResolveCaptureRecord>(
                    l, CaptureJsonOptions))
                .ToList();

            // Tick monotonic 0,1,2.
            Assert.Equal(0, records[0]!.Tick);
            Assert.Equal(1, records[1]!.Tick);
            Assert.Equal(2, records[2]!.Tick);

            // Inputs at tick 0 must match the harness's initial position.
            var firstInput = records[0]!.Input;
            Assert.Equal(InitialSphereWorld.X, firstInput.CurrentPos.X, 4);
            Assert.Equal(InitialSphereWorld.Y, firstInput.CurrentPos.Y, 4);
            Assert.Equal(InitialSphereWorld.Z, firstInput.CurrentPos.Z, 4);
            Assert.Equal(CellarId, firstInput.CellId);
            Assert.True(firstInput.IsOnGround,
                "First tick is seeded grounded.");

            // Body before + after snapshots present.
            Assert.NotNull(records[0]!.BodyBefore);
            Assert.NotNull(records[0]!.BodyAfter);

            var cpBefore = records[0]!.BodyBefore!.ContactPlane;
            Assert.Equal(0f, cpBefore.Normal.X, 5);
            Assert.Equal(0f, cpBefore.Normal.Y, 5);
            Assert.Equal(1f, cpBefore.Normal.Z, 5);
            Assert.Equal(-CellarFloorZ, cpBefore.D, 3);
        }
        finally
        {
            PhysicsResolveCapture.CapturePath = null;
            PhysicsResolveCapture.Close();
            if (File.Exists(capturePath))
                File.Delete(capturePath);
        }
    }

    [Fact]
    public void Capture_SkipsNonPlayerCalls()
    {
        string capturePath = Path.Combine(
            Path.GetTempPath(),
            $"acdream_capture_npc_{Guid.NewGuid():N}.jsonl");

        try
        {
            PhysicsResolveCapture.CapturePath = capturePath;
            PhysicsResolveCapture.ResetTickCounter();

            var (engine, _) = BuildEngineWithCellarFixtures();
            var body = BuildInitialBody();

            // Drive 3 ticks WITHOUT IsPlayer flag — simulates an NPC path.
            uint cellId = CellarId;
            bool isOnGround = true;
            for (int i = 0; i < 3; i++)
            {
                Vector3 target = body.Position + PerTickOffset;
                var result = engine.ResolveWithTransition(
                    body.Position, target, cellId,
                    SphereRadius, SphereHeight,
                    StepUpHeight, StepDownHeight,
                    isOnGround,
                    body: body,
                    moverFlags: ObjectInfoState.EdgeSlide,  // ← no IsPlayer
                    movingEntityId: 0);
                body.Position = result.Position;
                cellId = result.CellId;
                isOnGround = result.IsOnGround;
            }

            PhysicsResolveCapture.Close();

            Assert.False(File.Exists(capturePath),
                "Capture file should NOT exist when only non-player calls ran.");
        }
        finally
        {
            PhysicsResolveCapture.CapturePath = null;
            PhysicsResolveCapture.Close();
            if (File.Exists(capturePath))
                File.Delete(capturePath);
        }
    }

    public static readonly System.Text.Json.JsonSerializerOptions CaptureJsonOptions =
        new()
        {
            IncludeFields        = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };


    [Fact]
    public void LiveCompare_Tick0_Spawn()
    {
        var (engine, cache) = BuildEngineWithCellarFixtures();
        var captured = LoadCapturedRecord(record => record.Tick == 0);
        AssertCallMatchesCapture(engine, captured, expectStaleIsOnGroundEcho: true);
    }

    [Fact]
    public void LiveCompare_Tick376_OnRamp()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();
        var captured = LoadCapturedRecord(record => record.Tick == 376);
        AssertCallMatchesCapture(engine, captured, expectStaleIsOnGroundEcho: true);
    }

    [Fact]
    public void LiveCompare_FirstCap_FixClosesCottageFloorCap()
    {
        var (engine, _) = BuildEngineWithCellarFixtures();
        var captured = LoadCapturedRecord(record =>
            record.Result.CollisionNormalValid
            && record.Result.CollisionNormal.Z < -0.99f);

        Assert.True(captured.Result.CollisionNormalValid,
            "Captured record must have collisionNormalValid=true.");
        Assert.True(captured.Result.CollisionNormal.Z < -0.99f,
            $"Captured record must have downward collision normal; got " +
            $"{captured.Result.CollisionNormal}.");

        Assert.NotNull(captured.BodyBefore);
        var body = SeedBodyFromSnapshot(captured.BodyBefore);
        var harnessResult = engine.ResolveWithTransition(
            currentPos:     captured.Input.CurrentPos,
            targetPos:      captured.Input.TargetPos,
            cellId:         captured.Input.CellId,
            sphereRadius:   captured.Input.SphereRadius,
            sphereHeight:   captured.Input.SphereHeight,
            stepUpHeight:   captured.Input.StepUpHeight,
            stepDownHeight: captured.Input.StepDownHeight,
            isOnGround:     captured.Input.IsOnGround,
            body:           body,
            moverFlags:     (ObjectInfoState)captured.Input.MoverFlags,
            movingEntityId: captured.Input.MovingEntityId);

        Assert.False(
            harnessResult.CollisionNormalValid
                && harnessResult.CollisionNormal.Z < -0.99f,
            $"Floor clipping should prevent the downward-facing cottage-floor " +
            $"cap. Harness produced cn={harnessResult.CollisionNormal} " +
            $"(valid={harnessResult.CollisionNormalValid}). If z is back near " +
            $"-1, the GetNearbyObjects indoor-primary gate has regressed.");
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void LiveCompare_FirstCap_DiagnosticDump()
    {
        PhysicsDiagnostics.ProbeResolveEnabled    = true;
        PhysicsDiagnostics.ProbeIndoorBspEnabled  = true;
        PhysicsDiagnostics.ProbePolyDumpEnabled   = true;
        PhysicsDiagnostics.ProbePushBackEnabled   = true;
        PhysicsDiagnostics.ProbeStepWalkEnabled   = true;
        try
        {
            var (engine, cache) = BuildEngineWithCellarFixtures();

            DumpCellPolygons(cache, CellarId);
            DumpCellPolygons(cache, CottageNeighborA);
            DumpCellPolygons(cache, CottageNeighborB);

            var captured = LoadCapturedRecord(record =>
                record.Result.CollisionNormalValid
                && record.Result.CollisionNormal.Z < -0.99f);
            var body = SeedBodyFromSnapshot(captured.BodyBefore!);

            Console.WriteLine($"=== Replay tick {captured.Tick} ===");
            var result = engine.ResolveWithTransition(
                currentPos:     captured.Input.CurrentPos,
                targetPos:      captured.Input.TargetPos,
                cellId:         captured.Input.CellId,
                sphereRadius:   captured.Input.SphereRadius,
                sphereHeight:   captured.Input.SphereHeight,
                stepUpHeight:   captured.Input.StepUpHeight,
                stepDownHeight: captured.Input.StepDownHeight,
                isOnGround:     captured.Input.IsOnGround,
                body:           body,
                moverFlags:     (ObjectInfoState)captured.Input.MoverFlags,
                movingEntityId: captured.Input.MovingEntityId);

            Console.WriteLine(
                $"=== Result pos=({result.Position.X:F4},{result.Position.Y:F4},{result.Position.Z:F4}) " +
                $"cn=({result.CollisionNormal.X:F4},{result.CollisionNormal.Y:F4},{result.CollisionNormal.Z:F4}) " +
                $"cnValid={result.CollisionNormalValid} onGround={result.IsOnGround}");
        }
        finally
        {
            PhysicsDiagnostics.ProbeResolveEnabled    = false;
            PhysicsDiagnostics.ProbeIndoorBspEnabled  = false;
            PhysicsDiagnostics.ProbePolyDumpEnabled   = false;
            PhysicsDiagnostics.ProbePushBackEnabled   = false;
            PhysicsDiagnostics.ProbeStepWalkEnabled   = false;
        }
    }

    private static void DumpCellPolygons(PhysicsDataCache cache, uint cellId)
    {
        var cell = cache.GetCellStruct(cellId);
        if (cell is null)
        {
            Console.WriteLine($"[cell-dump] 0x{cellId:X8} NOT IN CACHE");
            return;
        }
        var t = cell.WorldTransform;
        Console.WriteLine($"[cell-dump] 0x{cellId:X8} resolved-poly-count={cell.Resolved.Count}");
        Console.WriteLine($"  WorldTransform.M14={t.M14:F4} M24={t.M24:F4} M34={t.M34:F4} (origin XYZ?)");
        Console.WriteLine($"  Translation=({t.Translation.X:F4},{t.Translation.Y:F4},{t.Translation.Z:F4})");
        foreach (var kv in cell.Resolved)
        {
            var p = kv.Value;
            string vertsWorld = "";
            if (p.Plane.Normal.Z > 0.9f || p.Plane.Normal.Z < -0.9f)
            {
                vertsWorld = " worldVerts=[" + string.Join(",", p.Vertices.Select(v =>
                {
                    var w = Vector3.Transform(v, cell.WorldTransform);
                    return $"({w.X:F2},{w.Y:F2},{w.Z:F2})";
                })) + "]";
            }
            Console.WriteLine(
                $"  poly id=0x{p.Id:X4} sides={p.SidesType} n=({p.Plane.Normal.X:F4},{p.Plane.Normal.Y:F4},{p.Plane.Normal.Z:F4}) d={p.Plane.D:F4} numV={p.NumPoints}{vertsWorld}");
        }
    }

    private static ResolveCaptureRecord LoadCapturedRecord(
        Func<ResolveCaptureRecord, bool> predicate)
    {
        var path = Path.Combine(FixtureDir, "live-capture.jsonl");
        Assert.True(File.Exists(path),
            $"Live-capture fixture missing: {path}. Re-run live capture " +
            $"with ACDREAM_CAPTURE_RESOLVE set.");

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = System.Text.Json.JsonSerializer
                .Deserialize<ResolveCaptureRecord>(line, CaptureJsonOptions)!;
            if (predicate(record))
                return record;
        }

        throw new Xunit.Sdk.XunitException(
            "No captured record matched the predicate. Update the fixture " +
            "to include a representative record.");
    }

    private static void AssertCallMatchesCapture(
        PhysicsEngine          engine,
        ResolveCaptureRecord   captured,
        bool                   expectStaleIsOnGroundEcho = false)
    {
        Assert.NotNull(captured.BodyBefore);
        Assert.NotNull(captured.BodyAfter);

        var body = SeedBodyFromSnapshot(captured.BodyBefore);

        var harnessResult = engine.ResolveWithTransition(
            currentPos:      captured.Input.CurrentPos,
            targetPos:       captured.Input.TargetPos,
            cellId:          captured.Input.CellId,
            sphereRadius:    captured.Input.SphereRadius,
            sphereHeight:    captured.Input.SphereHeight,
            stepUpHeight:    captured.Input.StepUpHeight,
            stepDownHeight:  captured.Input.StepDownHeight,
            isOnGround:      captured.Input.IsOnGround,
            body:            body,
            moverFlags:      (ObjectInfoState)captured.Input.MoverFlags,
            movingEntityId:  captured.Input.MovingEntityId);

        var divergences = new List<string>();

        // 1. Result fields
        AddIfDifferent(divergences, "Result.Position",
            captured.Result.Position, harnessResult.Position);
        AddIfDifferent(divergences, "Result.CellId",
            $"0x{captured.Result.CellId:X8}",
            $"0x{harnessResult.CellId:X8}");
        if (expectStaleIsOnGroundEcho)
        {
            AddIfDifferent(divergences, "Result.IsOnGround",
                false, harnessResult.IsOnGround);
        }
        else
        {
            AddIfDifferent(divergences, "Result.IsOnGround",
                captured.Result.IsOnGround, harnessResult.IsOnGround);
        }
        AddIfDifferent(divergences, "Result.CollisionNormalValid",
            captured.Result.CollisionNormalValid,
            harnessResult.CollisionNormalValid);
        if (captured.Result.CollisionNormalValid && harnessResult.CollisionNormalValid)
        {
            AddIfDifferent(divergences, "Result.CollisionNormal",
                captured.Result.CollisionNormal,
                harnessResult.CollisionNormal);
        }

        // 2. Body-after fields (subset that's most likely to diverge first)
        AddIfDifferent(divergences, "BodyAfter.Position",
            captured.BodyAfter.Position, body.Position);
        AddIfDifferent(divergences, "BodyAfter.ContactPlaneValid",
            captured.BodyAfter.ContactPlaneValid, body.ContactPlaneValid);
        if (captured.BodyAfter.ContactPlaneValid && body.ContactPlaneValid)
        {
            AddIfDifferent(divergences, "BodyAfter.ContactPlane.Normal",
                captured.BodyAfter.ContactPlane.Normal,
                body.ContactPlane.Normal);
            AddIfDifferent(divergences, "BodyAfter.ContactPlane.D",
                captured.BodyAfter.ContactPlane.D,
                body.ContactPlane.D);
        }
        AddIfDifferent(divergences, "BodyAfter.WalkablePolygonValid",
            captured.BodyAfter.WalkablePolygonValid, body.WalkablePolygonValid);
        AddIfDifferent(divergences, "BodyAfter.TransientState",
            $"0x{captured.BodyAfter.TransientState:X}",
            $"0x{(uint)body.TransientState:X}");

        if (divergences.Count > 0)
        {
            string summary = string.Join("\n  • ", divergences);
            string header = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Harness replay of captured tick {0} diverges from live engine. " +
                "Input: currentPos=({1:F4},{2:F4},{3:F4}) targetPos=({4:F4},{5:F4},{6:F4}) " +
                "cellId=0x{7:X8} isOnGround={8}",
                captured.Tick,
                captured.Input.CurrentPos.X, captured.Input.CurrentPos.Y, captured.Input.CurrentPos.Z,
                captured.Input.TargetPos.X,  captured.Input.TargetPos.Y,  captured.Input.TargetPos.Z,
                captured.Input.CellId, captured.Input.IsOnGround);
            throw new Xunit.Sdk.XunitException(
                header + "\nDivergences (live → harness):\n  • " + summary);
        }
    }

    private static PhysicsBody SeedBodyFromSnapshot(PhysicsBodySnapshot snap) => new()
    {
        Position             = snap.Position,
        Orientation          = snap.Orientation,
        Velocity             = snap.Velocity,
        Acceleration         = snap.Acceleration,
        Omega                = snap.Omega,
        GroundNormal         = snap.GroundNormal,
        SlidingNormal        = snap.SlidingNormal,
        ContactPlaneValid    = snap.ContactPlaneValid,
        ContactPlane         = snap.ContactPlane,
        ContactPlaneCellId   = snap.ContactPlaneCellId,
        ContactPlaneIsWater  = snap.ContactPlaneIsWater,
        WalkablePolygonValid = snap.WalkablePolygonValid,
        WalkablePlane        = snap.WalkablePlane,
        WalkableVertices     = snap.WalkableVertices,
        WalkableUp           = snap.WalkableUp,
        Elasticity           = snap.Elasticity,
        Friction             = snap.Friction,
        State                = (PhysicsStateFlags)snap.State,
        TransientState       = (TransientStateFlags)snap.TransientState,
        LastUpdateTime       = snap.LastUpdateTime,
    };

    private static void AddIfDifferent<T>(
        List<string> divergences, string name, T live, T harness)
    {
        if (EqualityComparer<T>.Default.Equals(live, harness))
            return;
        divergences.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: live={1} harness={2}", name, live, harness));
    }

    private static void AddIfDifferent(
        List<string> divergences, string name, Vector3 live, Vector3 harness)
    {
        if (Vector3.DistanceSquared(live, harness) < 1e-6f)
            return;
        divergences.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: live=({1:F4},{2:F4},{3:F4}) harness=({4:F4},{5:F4},{6:F4})",
            name, live.X, live.Y, live.Z, harness.X, harness.Y, harness.Z));
    }

    private static void AddIfDifferent(
        List<string> divergences, string name, float live, float harness)
    {
        if (MathF.Abs(live - harness) < 1e-3f)
            return;
        divergences.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: live={1:F4} harness={2:F4}", name, live, harness));
    }

    // ───────────────────────────────────────────────────────────────
    // Harness internals
    // ───────────────────────────────────────────────────────────────

    public sealed record TrajectoryPoint(
        int     Tick,
        Vector3 Position,
        uint    CellId,
        bool    IsOnGround,
        bool    CpValid);

    private static (PhysicsEngine engine, PhysicsDataCache cache)
        BuildEngineWithCellarFixtures()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        foreach (var cellId in new[] { CellarId, CottageNeighborA, CottageNeighborB })
        {
            var path = Path.Combine(FixtureDir, $"0x{cellId:X8}.json");
            Assert.True(File.Exists(path),
                $"Fixture missing: {path}. Re-run cell-dump capture " +
                $"(commit 3f56915 captured the originals).");
            var dump = CellDumpSerializer.Read(path);
            var cell = CellDumpSerializer.Hydrate(dump);
            var cellWithBsp = AttachSyntheticBsp(cell);
            cache.RegisterCellStructForTest(cellId, cellWithBsp);
        }

        var heights      = new byte[81];
        var heightTable  = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        var stubTerrain  = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(
            landblockId:    0xA9B40000u,
            terrain:        stubTerrain,
            cells:          Array.Empty<CellSurface>(),
            portals:        Array.Empty<PortalPlane>(),
            worldOffsetX:   0f,
            worldOffsetY:   0f);

        RegisterCottageGfxObj(engine, cache);

        return (engine, cache);
    }

    private static CellPhysics AttachSyntheticBsp(CellPhysics cell)
    {
        var bsphereCenter = new Vector3(0f, 0f, 0f);
        var bsphereRadius = 15f;

        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = bsphereCenter, Radius = bsphereRadius },
        };
        foreach (var kv in cell.Resolved)
            leaf.Polygons.Add(kv.Key);

        var bspTree = new PhysicsBSPTree { Root = leaf };

        return new CellPhysics
        {
            BSP                    = bspTree,
            PhysicsPolygons        = cell.PhysicsPolygons,
            Vertices               = cell.Vertices,
            WorldTransform         = cell.WorldTransform,
            InverseWorldTransform  = cell.InverseWorldTransform,
            Resolved               = cell.Resolved,
            CellBSP                = cell.CellBSP,
            Portals                = cell.Portals,
            PortalPolygons         = cell.PortalPolygons,
            VisibleCellIds         = cell.VisibleCellIds,
        };
    }

    private static void RegisterCottageGfxObj(PhysicsEngine engine, PhysicsDataCache cache)
    {
        const uint CottageGfxId    = 0x01000A2Bu;
        const uint CottageEntityId = 0x00A9B479u;

        var fixturePath = Path.Combine(FixtureDir, "0x01000A2B.gfxobj.json");
        Assert.True(File.Exists(fixturePath),
            $"Cottage GfxObj fixture missing: {fixturePath}. Re-run live " +
            $"capture with ACDREAM_DUMP_GFXOBJS=0x01000A2B.");

        var dump     = GfxObjDumpSerializer.Read(fixturePath);
        var physics  = GfxObjDumpSerializer.Hydrate(dump);
        cache.RegisterGfxObjForTest(CottageGfxId, physics);

        var worldPos = new Vector3(130.5f, 11.5f, 94.0f);
        var worldRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        engine.ShadowObjects.Register(
            entityId:      CottageEntityId,
            gfxObjId:      CottageGfxId,
            worldPos:      worldPos,
            rotation:      worldRot,
            radius:        physics.BoundingSphere?.Radius ?? 14f,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   0xA9B40000u,
            collisionType: ShadowCollisionType.BSP,
            scale:         1.0f,
            seedCellId:    0u);
    }

    private static PhysicsBody BuildInitialBody() => new()
    {
        Position           = InitialSphereWorld,
        Orientation        = Quaternion.Identity,

        ContactPlaneValid  = true,
        ContactPlane       = new System.Numerics.Plane(0f, 0f, 1f, -CellarFloorZ),
        ContactPlaneCellId = CellarId,

        WalkablePolygonValid = true,
        WalkablePlane        = new System.Numerics.Plane(0f, 0f, 1f, -CellarFloorZ),
        WalkableVertices     = new[]
        {
            new Vector3(142.1f, 11.5f, 90.95f),
            new Vector3(142.1f,  8.4f, 90.95f),
            new Vector3(140.1f,  8.4f, 90.95f),
            new Vector3(140.1f, 11.5f, 90.95f),
        },
        WalkableUp           = Vector3.UnitZ,

        TransientState       = TransientStateFlags.Contact
                             | TransientStateFlags.OnWalkable,
    };

    private static List<TrajectoryPoint> SimulateTicks(
        PhysicsEngine engine,
        PhysicsBody   body,
        uint          initialCellId,
        int           tickCount)
    {
        uint cellId      = initialCellId;
        bool isOnGround  = true;

        var trajectory = new List<TrajectoryPoint>(tickCount + 1)
        {
            new(0, body.Position, cellId, isOnGround, body.ContactPlaneValid),
        };

        for (int tick = 1; tick <= tickCount; tick++)
        {
            Vector3 target = body.Position + PerTickOffset;

            var result = engine.ResolveWithTransition(
                currentPos:      body.Position,
                targetPos:       target,
                cellId:          cellId,
                sphereRadius:    SphereRadius,
                sphereHeight:    SphereHeight,
                stepUpHeight:    StepUpHeight,
                stepDownHeight:  StepDownHeight,
                isOnGround:      isOnGround,
                body:            body,
                moverFlags:      ObjectInfoState.IsPlayer
                                 | ObjectInfoState.EdgeSlide,
                movingEntityId:  0);

            body.Position = result.Position;
            cellId        = result.CellId;
            isOnGround    = result.IsOnGround;

            trajectory.Add(new(
                tick,
                body.Position,
                cellId,
                isOnGround,
                body.ContactPlaneValid));
        }

        return trajectory;
    }

    private static string FixtureDir =>
        Path.Combine(SolutionRoot(), "tests", "AcDream.Core.Tests",
                     "Fixtures", "cellar-ascent");

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate AcDream.slnx from " + AppContext.BaseDirectory);
    }
}
