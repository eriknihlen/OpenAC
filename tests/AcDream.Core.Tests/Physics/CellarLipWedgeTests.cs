using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellarLipWedgeTests
{
    private const uint CottageFloorId = 0xA9B40171u;
    private const uint Connector74Id  = 0xA9B40174u;
    private const uint ThresholdId    = 0xA9B40175u;   // 0.364 m threshold slab

    // Player physics from PlayerMovementController.cs (human, from Setup).
    private const float SphereRadius   = 0.48f;
    private const float SphereHeight   = 1.20f;
    private const float StepUpHeight   = 0.40f;
    private const float StepDownHeight = 0.04f;

    private static readonly Vector3 WedgeSphereCenter = new(153.406f, 9.754f, 93.936f);
    private static readonly Vector3 WedgeBodyPos =
        new(153.406f, 9.754f, 93.936f - SphereRadius);   // foot bottom Z=93.456
    private static readonly Vector3 PerTickOffset = new(0f, -0.10f, 0f);

    private const float CottageFloorZ   = 94.00f;
    private const float RestOnCottageZ  = CottageFloorZ + SphereRadius; // ≈94.48

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_DriveOffThreshold_DumpTrajectory()
    {
        PhysicsDiagnostics.ProbeResolveEnabled    = true;
        PhysicsDiagnostics.ProbeIndoorBspEnabled  = true;
        var saved = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var engine = BuildEngineWithLipFixtures();
            var body   = BuildWedgeBody();
            var traj   = SimulateTicks(engine, body, ThresholdId, 4);

            Console.SetOut(saved);
            var probeLines = sw.ToString();
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "lip-wedge-diag.log"),
                "TRAJECTORY:\n" + string.Join("\n", traj.Select(p =>
                    $"tick={p.Tick} pos=({p.Position.X:F4},{p.Position.Y:F4},{p.Position.Z:F4}) " +
                    $"cell=0x{p.CellId:X8} onGround={p.IsOnGround} cpValid={p.CpValid}")) +
                "\n\nPROBES:\n" + probeLines);
        }
        finally
        {
            Console.SetOut(saved);
            PhysicsDiagnostics.ProbeResolveEnabled   = false;
            PhysicsDiagnostics.ProbeIndoorBspEnabled = false;
        }
    }

    [Fact]
    public void DocumentsWedge_PlayerFrozenAtThreshold_BlockedByMinusXWall()
    {
        var engine = BuildEngineWithLipFixtures();
        var body   = BuildWedgeBody();
        var traj   = SimulateTicks(engine, body, ThresholdId, 30);

        var final = traj[^1];
        float yAdvance = WedgeBodyPos.Y - final.Position.Y;
        float zRise    = final.Position.Z - WedgeBodyPos.Z;

        Assert.True(
            yAdvance < 0.1f && zRise < 0.1f,
            $"DOCUMENTS-THE-BUG: expected the player to be FROZEN at the threshold " +
            $"(the −X-wall wedge). Instead it advanced to " +
            $"({final.Position.X:F3},{final.Position.Y:F3},{final.Position.Z:F3}) " +
            $"after 30 ticks (yAdvance={yAdvance:F3}, zRise={zRise:F3}). If the wedge " +
            $"fix landed, FLIP this assertion to require the climb " +
            $"(Z≥{CottageFloorZ - 0.05f:F2}, yAdvance>0.5).");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static PhysicsBody BuildWedgeBody() => new()
    {
        Position           = WedgeBodyPos,           // foot bottom Z=93.456
        Orientation        = Quaternion.Identity,

        ContactPlaneValid    = true,
        ContactPlane         = new System.Numerics.Plane(0f, 0f, 1f, -WedgeBodyPos.Z),
        ContactPlaneCellId   = ThresholdId,
        WalkablePolygonValid = true,
        WalkablePlane        = new System.Numerics.Plane(0f, 0f, 1f, -WedgeBodyPos.Z),
        WalkableVertices     = new[]
        {
            new Vector3(WedgeBodyPos.X - 1f, WedgeBodyPos.Y - 1f, WedgeBodyPos.Z),
            new Vector3(WedgeBodyPos.X - 1f, WedgeBodyPos.Y + 1f, WedgeBodyPos.Z),
            new Vector3(WedgeBodyPos.X + 1f, WedgeBodyPos.Y + 1f, WedgeBodyPos.Z),
            new Vector3(WedgeBodyPos.X + 1f, WedgeBodyPos.Y - 1f, WedgeBodyPos.Z),
        },
        WalkableUp           = Vector3.UnitZ,
        TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
    };

    private static List<TrajPoint> SimulateTicks(
        PhysicsEngine engine, PhysicsBody body, uint initialCellId, int tickCount)
    {
        uint cellId     = initialCellId;
        bool isOnGround  = true;
        var traj = new List<TrajPoint> { new(0, body.Position, cellId, isOnGround, body.ContactPlaneValid) };

        for (int tick = 1; tick <= tickCount; tick++)
        {
            Vector3 target = body.Position + PerTickOffset;
            var result = engine.ResolveWithTransition(
                currentPos:     body.Position,
                targetPos:      target,
                cellId:         cellId,
                sphereRadius:   SphereRadius,
                sphereHeight:   SphereHeight,
                stepUpHeight:   StepUpHeight,
                stepDownHeight: StepDownHeight,
                isOnGround:     isOnGround,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            body.Position = result.Position;
            cellId        = result.CellId;
            isOnGround    = result.IsOnGround;
            traj.Add(new(tick, body.Position, cellId, isOnGround, body.ContactPlaneValid));
        }
        return traj;
    }

    private sealed record TrajPoint(int Tick, Vector3 Position, uint CellId, bool IsOnGround, bool CpValid);


    private static readonly System.Text.Json.JsonSerializerOptions WedgeJsonOptions =
        new() { IncludeFields = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private static List<ResolveCaptureRecord> LoadWedgeRecords()
    {
        var path = Path.Combine(FixtureDir, "wedge-records.jsonl");
        Assert.True(File.Exists(path), $"Wedge fixture missing: {path}");
        var list = new List<ResolveCaptureRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            list.Add(System.Text.Json.JsonSerializer.Deserialize<ResolveCaptureRecord>(line, WedgeJsonOptions)!);
        }
        return list;
    }

    private static PhysicsBody SeedBody(PhysicsBodySnapshot s) => new()
    {
        Position             = s.Position,
        Orientation          = s.Orientation,
        Velocity             = s.Velocity,
        Acceleration         = s.Acceleration,
        Omega                = s.Omega,
        GroundNormal         = s.GroundNormal,
        SlidingNormal        = s.SlidingNormal,
        ContactPlaneValid    = s.ContactPlaneValid,
        ContactPlane         = s.ContactPlane,
        ContactPlaneCellId   = s.ContactPlaneCellId,
        ContactPlaneIsWater  = s.ContactPlaneIsWater,
        WalkablePolygonValid = s.WalkablePolygonValid,
        WalkablePlane        = s.WalkablePlane,
        WalkableVertices     = s.WalkableVertices,
        WalkableUp           = s.WalkableUp,
        Elasticity           = s.Elasticity,
        Friction             = s.Friction,
        State                = (PhysicsStateFlags)s.State,
        TransientState       = (TransientStateFlags)s.TransientState,
        LastUpdateTime       = s.LastUpdateTime,
    };

    private static (Vector3 res, float requested, float advance) ReplayRecord(ResolveCaptureRecord rec)
    {
        var engine = BuildEngineWithLipFixtures();
        var body   = SeedBody(rec.BodyBefore!);
        var result = engine.ResolveWithTransition(
            currentPos:     rec.Input.CurrentPos,
            targetPos:      rec.Input.TargetPos,
            cellId:         rec.Input.CellId,
            sphereRadius:   rec.Input.SphereRadius,
            sphereHeight:   rec.Input.SphereHeight,
            stepUpHeight:   rec.Input.StepUpHeight,
            stepDownHeight: rec.Input.StepDownHeight,
            isOnGround:     rec.Input.IsOnGround,
            body:           body,
            moverFlags:     (ObjectInfoState)rec.Input.MoverFlags,
            movingEntityId: rec.Input.MovingEntityId);
        float requested = Vector3.Distance(rec.Input.CurrentPos, rec.Input.TargetPos);
        float advance   = Vector3.Distance(rec.Input.CurrentPos, result.Position);
        return (result.Position, requested, advance);
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_ReplayLiveWedgeRecords_Advance()
    {
        var recs = LoadWedgeRecords();
        var lines = new List<string>();
        foreach (var (rec, i) in recs.Select((r, i) => (r, i)))
        {
            if (rec.BodyBefore is null) continue;
            var (res, req, adv) = ReplayRecord(rec);
            var cpN = rec.BodyBefore.ContactPlane.Normal;
            lines.Add($"#{i} cp=({cpN.X:F2},{cpN.Y:F2},{cpN.Z:F2}) req={req:F3} adv={adv:F3} ({(req>0?100*adv/req:0):F0}%) " +
                      $"cur=({rec.Input.CurrentPos.X:F2},{rec.Input.CurrentPos.Y:F2},{rec.Input.CurrentPos.Z:F2}) " +
                      $"tgt=({rec.Input.TargetPos.X:F2},{rec.Input.TargetPos.Y:F2},{rec.Input.TargetPos.Z:F2}) " +
                      $"res=({res.X:F2},{res.Y:F2},{res.Z:F2})");
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lip-wedge-replay.log"), string.Join("\n", lines));
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_ReplayFloorCpRecord_StepUpProbes()
    {
        var rec = LoadWedgeRecords().First(r => r.BodyBefore is not null
                                             && r.BodyBefore.ContactPlane.Normal.Z > 0.99f);
        var saved = Console.Out;
        var sw = new StringWriter();
        PhysicsDiagnostics.ProbeIndoorBspEnabled = true;
        PhysicsDiagnostics.ProbeStepWalkEnabled  = true;
        Console.SetOut(sw);
        try
        {
            var (res, req, adv) = ReplayRecord(rec);
            Console.SetOut(saved);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "lip-wedge-stepup.log"),
                $"record cur=({rec.Input.CurrentPos.X:F4},{rec.Input.CurrentPos.Y:F4},{rec.Input.CurrentPos.Z:F4}) " +
                $"tgt=({rec.Input.TargetPos.X:F4},{rec.Input.TargetPos.Y:F4},{rec.Input.TargetPos.Z:F4}) " +
                $"req={req:F3} adv={adv:F3} res=({res.X:F4},{res.Y:F4},{res.Z:F4})\n\n" + sw.ToString());
        }
        finally
        {
            Console.SetOut(saved);
            PhysicsDiagnostics.ProbeIndoorBspEnabled = false;
            PhysicsDiagnostics.ProbeStepWalkEnabled  = false;
        }
    }

    [Fact]
    public void Fix_StaleFootCenter_RampRecordClimbsCottageFloor()
    {
        var rec = LoadWedgeRecords()[9];
        var (res, requested, advance) = ReplayRecord(rec);
        Assert.True(advance > 0.25f * requested && res.Z >= CottageFloorZ - 0.05f,
            $"Expected ramp record #9 to climb onto the cottage floor after the " +
            $"stale-footCenter fix. advance={advance:F3} (req={requested:F3}), " +
            $"res=({res.X:F3},{res.Y:F3},{res.Z:F3}); want advance>0.25·req and Z≥{CottageFloorZ - 0.05f:F2}.");
    }

    [Fact]
    public void Fix_StaleFootCenter_MajorityOfWedgeRecordsAdvance()
    {
        var recs = LoadWedgeRecords();
        int advanced = 0, total = 0;
        foreach (var rec in recs)
        {
            if (rec.BodyBefore is null) continue;
            total++;
            var (res, req, adv) = ReplayRecord(rec);
            if (adv > 0.25f * req) advanced++;
        }
        Assert.True(advanced >= 18,
            $"Expected ≥18 of {total} captured wedge records to advance >0.25·req " +
            $"after the stale-footCenter fix; got {advanced}.");
    }

    [Fact]
    public void DocumentsResidualWedge_LiveFloorCp_SlidingNormalKillsPlusY()
    {
        var recs = LoadWedgeRecords();
        var rec  = recs.First(r => r.BodyBefore is not null
                                && r.BodyBefore.ContactPlane.Normal.Z > 0.99f);
        var (res, requested, advance) = ReplayRecord(rec);
        var c = rec.Input.CurrentPos; var t = rec.Input.TargetPos;
        Assert.True(advance < 0.1f * requested,
            $"DOCUMENTS-RESIDUAL: expected the player STUCK (sliding-normal +Y-kill). " +
            $"Instead it advanced: cur=({c.X:F3},{c.Y:F3},{c.Z:F3}) tgt=({t.X:F3},{t.Y:F3},{t.Z:F3}) " +
            $"res=({res.X:F3},{res.Y:F3},{res.Z:F3}) requested={requested:F3} advance={advance:F3}. " +
            $"If the slide +Y-kill residual is fixed, FLIP this to require advance>0.25·requested.");
    }

    private static PhysicsEngine BuildEngineWithLipFixtures()
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        foreach (var cellId in new[] { CottageFloorId, Connector74Id, ThresholdId })
        {
            var path = Path.Combine(FixtureDir, $"0x{cellId:X8}.json");
            Assert.True(File.Exists(path), $"Lip fixture missing: {path}");
            var dump = CellDumpSerializer.Read(path);
            var cell = CellDumpSerializer.Hydrate(dump);
            cache.RegisterCellStructForTest(cellId, AttachSyntheticBsp(cell));
        }

        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:  0xA9B40000u,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        return engine;
    }

    private static CellPhysics AttachSyntheticBsp(CellPhysics cell)
    {
        var leaf = new PhysicsBSPNode
        {
            Type = BSPNodeType.Leaf,
            BoundingSphere = new Sphere { Origin = new Vector3(0f, 0f, 0f), Radius = 15f },
        };
        foreach (var kv in cell.Resolved)
            leaf.Polygons.Add(kv.Key);

        return new CellPhysics
        {
            BSP                   = new PhysicsBSPTree { Root = leaf },
            PhysicsPolygons       = cell.PhysicsPolygons,
            Vertices              = cell.Vertices,
            WorldTransform        = cell.WorldTransform,
            InverseWorldTransform = cell.InverseWorldTransform,
            Resolved              = cell.Resolved,
            CellBSP               = cell.CellBSP,
            Portals               = cell.Portals,
            PortalPolygons        = cell.PortalPolygons,
            VisibleCellIds        = cell.VisibleCellIds,
        };
    }

    private static string FixtureDir =>
        Path.Combine(SolutionRoot(), "tests", "AcDream.Core.Tests", "Fixtures", "cellar-lip");

    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate solution root (AcDream.slnx).");
    }
}
