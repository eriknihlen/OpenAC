using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using Xunit;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

public class DoorBugTrajectoryReplayTests
{
    private const uint DoorEntityId    = 0x000F4246u;
    private const uint DoorGfxObjId    = 0x010044B5u;
    private const uint DoorClosedState = 0x00010008u; // HAS_PHYSICS_BSP | REPORT_COLLISIONS
    private const uint DoorLandblockId = 0xA9B40000u;

    private static readonly Vector3 BspWorldPos = new(132.57f, 16.99f, 95.36f);
    private const float BspRadius = 1.975f;

    private static readonly Vector3 CylWorldPos = new(132.56f, 17.11f, 94.10f);
    private const float CylRadius = 0.10f;
    private const float CylHeight = 0.20f;

    // ── Tests ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void LiveCompare_DoorOffCenterWalkthrough_Tick13558()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var (engine, _) = BuildEngineWithDoorFixture(datDir);
        var captured = LoadCapturedRecord(r => r.Tick == 13558);
        var (result, _) = ReplayCapturedCall(engine, captured);

        Assert.True(result.CollisionNormalValid,
            $"Door must block the indoor-side off-center approach; " +
            $"pos=({result.Position.X:F4},{result.Position.Y:F4},{result.Position.Z:F4})");
        Assert.True(result.Position.Y <= 17.0f,
            $"Sphere must not cross the slab; pos.Y={result.Position.Y:F4} " +
            $"(capture's walkthrough reached 17.2041)");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void LiveCompare_DoorBlocksFromOutside_Tick22760()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var (engine, _) = BuildEngineWithDoorFixture(datDir);
        var captured = LoadCapturedRecord(r => r.Tick == 22760);
        var (result, _) = ReplayCapturedCall(engine, captured);

        Assert.True(result.CollisionNormalValid,
            $"Door must block the outdoor-side approach; " +
            $"pos=({result.Position.X:F4},{result.Position.Y:F4},{result.Position.Z:F4})");
        Assert.True(result.Position.Y >= 17.95f,
            $"Sphere must not penetrate southward past the slab; " +
            $"pos.Y={result.Position.Y:F4} (live blocked at 18.0183)");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_Tick22760_DumpEngineInternals()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        PhysicsDiagnostics.ProbeResolveEnabled   = true;
        PhysicsDiagnostics.ProbeBuildingEnabled  = true;
        PhysicsDiagnostics.ProbeIndoorBspEnabled = true;
        PhysicsDiagnostics.ProbePushBackEnabled  = true;
        PhysicsDiagnostics.ProbeStepWalkEnabled  = true;
        try
        {
            var (engine, _) = BuildEngineWithDoorFixture(datDir);
            var captured = LoadCapturedRecord(r => r.Tick == 22760);
            var body = SeedBodyFromSnapshot(captured.BodyBefore!);

            Console.WriteLine("=== Replay tick 22760 (outdoor-side block) ===");
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

            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "=== Harness: pos=({0:F4},{1:F4},{2:F4}) cn=({3:F4},{4:F4},{5:F4}) cnValid={6} onGround={7} cell=0x{8:X8}",
                result.Position.X, result.Position.Y, result.Position.Z,
                result.CollisionNormal.X, result.CollisionNormal.Y, result.CollisionNormal.Z,
                result.CollisionNormalValid, result.IsOnGround, result.CellId));
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "=== Live:    pos=({0:F4},{1:F4},{2:F4}) cn=({3:F4},{4:F4},{5:F4}) cnValid={6} onGround={7} cell=0x{8:X8}",
                captured.Result.Position.X, captured.Result.Position.Y, captured.Result.Position.Z,
                captured.Result.CollisionNormal.X, captured.Result.CollisionNormal.Y, captured.Result.CollisionNormal.Z,
                captured.Result.CollisionNormalValid, captured.Result.IsOnGround, captured.Result.CellId));
        }
        finally
        {
            PhysicsDiagnostics.ProbeResolveEnabled   = false;
            PhysicsDiagnostics.ProbeBuildingEnabled  = false;
            PhysicsDiagnostics.ProbeIndoorBspEnabled = false;
            PhysicsDiagnostics.ProbePushBackEnabled  = false;
            PhysicsDiagnostics.ProbeStepWalkEnabled  = false;
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_Tick13558_DumpEngineInternals()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        PhysicsDiagnostics.ProbeResolveEnabled   = true;
        PhysicsDiagnostics.ProbeBuildingEnabled  = true;
        PhysicsDiagnostics.ProbeIndoorBspEnabled = true;
        PhysicsDiagnostics.ProbePushBackEnabled  = true;
        PhysicsDiagnostics.ProbeStepWalkEnabled  = true;
        try
        {
            var (engine, _) = BuildEngineWithDoorFixture(datDir);
            var captured = LoadCapturedRecord(r => r.Tick == 13558);
            var body = SeedBodyFromSnapshot(captured.BodyBefore!);

            Console.WriteLine("=== Replay tick 13558 (the walkthrough) ===");
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

            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "=== Harness: pos=({0:F4},{1:F4},{2:F4}) cn=({3:F4},{4:F4},{5:F4}) cnValid={6} onGround={7} cell=0x{8:X8}",
                result.Position.X, result.Position.Y, result.Position.Z,
                result.CollisionNormal.X, result.CollisionNormal.Y, result.CollisionNormal.Z,
                result.CollisionNormalValid, result.IsOnGround, result.CellId));
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "=== Live:    pos=({0:F4},{1:F4},{2:F4}) cn=({3:F4},{4:F4},{5:F4}) cnValid={6} onGround={7} cell=0x{8:X8}",
                captured.Result.Position.X, captured.Result.Position.Y, captured.Result.Position.Z,
                captured.Result.CollisionNormal.X, captured.Result.CollisionNormal.Y, captured.Result.CollisionNormal.Z,
                captured.Result.CollisionNormalValid, captured.Result.IsOnGround, captured.Result.CellId));
        }
        finally
        {
            PhysicsDiagnostics.ProbeResolveEnabled   = false;
            PhysicsDiagnostics.ProbeBuildingEnabled  = false;
            PhysicsDiagnostics.ProbeIndoorBspEnabled = false;
            PhysicsDiagnostics.ProbePushBackEnabled  = false;
            PhysicsDiagnostics.ProbeStepWalkEnabled  = false;
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void FindTransitCellsSphere_IndoorExitPortal_AddsOutsideForCapturedSpherePos()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint CellId       = 0xA9B40150u;
        const uint EnvCellPrefix = 0x0D000000u;

        var envCell = dats.Get<DatReaderWriter.DBObjs.EnvCell>(CellId);
        Assert.NotNull(envCell);

        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(
            EnvCellPrefix | envCell!.EnvironmentId);
        Assert.NotNull(environment);
        Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cellStruct));
        Assert.NotNull(cellStruct);

        var cellOriginWorld = envCell.Position.Origin;
        var worldTransform =
            Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation) *
            Matrix4x4.CreateTranslation(cellOriginWorld);

        var cache = new PhysicsDataCache();
        cache.CacheCellStruct(CellId, envCell, cellStruct!, worldTransform);

        var cellPhysics = cache.GetCellStruct(CellId);
        Assert.NotNull(cellPhysics);
        Assert.NotNull(cellPhysics!.Portals);
        Assert.Contains(cellPhysics.Portals, p => p.OtherCellId == 0xFFFFu);

        var sphereWorld = new Vector3(132.3603f, 16.8113f, 94.0000f);
        const float sphereRadius = 0.48f;

        Assert.NotNull(cellPhysics.CellBSP);
        var localCenter = Vector3.Transform(sphereWorld, cellPhysics.InverseWorldTransform);

        // ── 5. Run FindTransitCellsSphere — does it fire exitOutside? ─
        var candidates = new HashSet<uint>();
        CellTransit.FindTransitCellsSphere(
            cache, cellPhysics, CellId,
            sphereWorld, sphereRadius,
            candidates,
            out bool exitOutside);

        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "localCenter=({0:F4},{1:F4},{2:F4}) radius={3:F4}",
            localCenter.X, localCenter.Y, localCenter.Z, sphereRadius));
        foreach (var portal in cellPhysics.Portals)
        {
            if (cellPhysics.PortalPolygons is null
                || !cellPhysics.PortalPolygons.TryGetValue(portal.PolygonId, out var poly))
                continue;
            float dist = Vector3.Dot(localCenter, poly.Plane.Normal) + poly.Plane.D;
            float rad = sphereRadius + 0.02f;
            bool hit = dist > -rad && dist < rad;
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "  portal otherCellId=0x{0:X4} polyId=0x{1:X4} n=({2:F4},{3:F4},{4:F4}) d={5:F4} dist={6:F4} rad={7:F4} hit={8}",
                portal.OtherCellId, portal.PolygonId,
                poly.Plane.Normal.X, poly.Plane.Normal.Y, poly.Plane.Normal.Z,
                poly.Plane.D, dist, rad, hit));
        }

        Assert.True(exitOutside,
            "Captured sphere at tick 13558 is straddling cell 0xA9B40150's " +
            "exit portal (0xFFFF) plane. FindTransitCellsSphere should fire " +
            "exitOutside, but did not. Candidates returned: " +
            string.Join(",", candidates.Select(c => $"0x{c:X8}")));
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InsideOut_Tick3254_WithCottageWalls_ShouldBlock()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var (engine, cache) = BuildFaithfulDoorEngine(datDir);

        const uint CottageGfxId    = 0x01000A2Bu;
        const uint CottageEntityId = 0x00A9B479u;
        var cottageFixturePath = Path.Combine(SolutionRoot(),
            "tests", "AcDream.Core.Tests", "Fixtures", "cellar-ascent",
            "0x01000A2B.gfxobj.json");
        Assert.True(File.Exists(cottageFixturePath));
        var cottageDump    = GfxObjDumpSerializer.Read(cottageFixturePath);
        var cottagePhysics = GfxObjDumpSerializer.Hydrate(cottageDump);
        cache.RegisterGfxObjForTest(CottageGfxId, cottagePhysics);

        engine.ShadowObjects.Register(
            entityId:      CottageEntityId,
            gfxObjId:      CottageGfxId,
            worldPos:      new Vector3(130.5f, 11.5f, 94.0f),
            rotation:      Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
            radius:        cottagePhysics.BoundingSphere?.Radius ?? 14f,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   DoorLandblockId,
            collisionType: ShadowCollisionType.BSP,
            scale:         1.0f,
            seedCellId:    0u);

        var currentPos = new Vector3(133.65524f, 17.58999f, 94f);
        var targetPos  = new Vector3(133.54903f, 17.599283f, 94f);
        var (result, body) = ResolveAt(engine, currentPos, targetPos, 0xA9B40150u);

        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Harness tick 3254 reply: pos=({0:F4},{1:F4},{2:F4}) cn=({3:F4},{4:F4},{5:F4}) " +
            "cnValid={6} cell=0x{7:X8}",
            result.Position.X, result.Position.Y, result.Position.Z,
            result.CollisionNormal.X, result.CollisionNormal.Y, result.CollisionNormal.Z,
            result.CollisionNormalValid, result.CellId));

        Assert.True(result.Position.Y < targetPos.Y - 0.005f,
            $"BUG REPRODUCTION: harness allowed Y motion ({result.Position.Y}) toward " +
            $"target ({targetPos.Y}). Cottage wall should block sphere at X=133.655 " +
            $"(0.095 m east of slab east edge). If this assertion FAILS, the cottage " +
            $"wall is now blocking as expected — the floor-clipping correction is active.");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void CornerSlide_AlcoveEastToCottageNorth_ShouldBlock()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var (engine, cache) = BuildFaithfulDoorEngine(datDir);

        const uint CottageGfxId    = 0x01000A2Bu;
        const uint CottageEntityId = 0x00A9B479u;
        var cottageFixturePath = Path.Combine(SolutionRoot(),
            "tests", "AcDream.Core.Tests", "Fixtures", "cellar-ascent",
            "0x01000A2B.gfxobj.json");
        var cottageDump    = GfxObjDumpSerializer.Read(cottageFixturePath);
        var cottagePhysics = GfxObjDumpSerializer.Hydrate(cottageDump);
        cache.RegisterGfxObjForTest(CottageGfxId, cottagePhysics);

        engine.ShadowObjects.Register(
            entityId:      CottageEntityId,
            gfxObjId:      CottageGfxId,
            worldPos:      new Vector3(130.5f, 11.5f, 94.0f),
            rotation:      Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
            radius:        cottagePhysics.BoundingSphere?.Radius ?? 14f,
            worldOffsetX:  0f,
            worldOffsetY:  0f,
            landblockId:   DoorLandblockId,
            collisionType: ShadowCollisionType.BSP,
            scale:         1.0f,
            seedCellId:    0u);

        const uint AlcoveCellId = 0xA9B40150u;
        using (var dats = new DatCollection(datDir, DatAccessType.Read))
        {
            var envCell = dats.Get<DatReaderWriter.DBObjs.EnvCell>(AlcoveCellId);
            Assert.NotNull(envCell);
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(
                0x0D000000u | envCell!.EnvironmentId);
            Assert.NotNull(environment);
            Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cellStruct));

            var cellOriginWorld = envCell.Position.Origin;
            var cellTransform =
                Matrix4x4.CreateFromQuaternion(envCell.Position.Orientation) *
                Matrix4x4.CreateTranslation(cellOriginWorld);
            cache.CacheCellStruct(AlcoveCellId, envCell, cellStruct!, cellTransform);
        }
        Assert.NotNull(cache.GetCellStruct(AlcoveCellId));

        // 3. Sphere setup: inside alcove, near east wall.
        // Alcove east wall at world X=133.5, Y=[16.5, 17.1]. Sphere at
        // X=132.95 (sphere east edge 133.43 just west of wall), Y=16.8
        // (inside alcove Y range).
        var currentPos = new Vector3(132.95f, 16.8f, 94f);

        Vector3 pos = currentPos;
        uint    cellId = AlcoveCellId;
        bool    isOnGround = true;

        var body = new PhysicsBody
        {
            Position             = pos,
            Orientation          = Quaternion.Identity,
            ContactPlaneValid    = true,
            ContactPlane         = new System.Numerics.Plane(0f, 0f, 1f, -94f),
            ContactPlaneCellId   = cellId,
            WalkablePolygonValid = true,
            WalkablePlane        = new System.Numerics.Plane(0f, 0f, 1f, -94f),
            WalkableVertices     = new[]
            {
                new Vector3(120f, 10f, 94f),
                new Vector3(145f, 10f, 94f),
                new Vector3(145f, 30f, 94f),
                new Vector3(120f, 30f, 94f),
            },
            WalkableUp           = Vector3.UnitZ,
            TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };

        Console.WriteLine($"Start: pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) cell=0x{cellId:X8}");
        for (int t = 1; t <= 50; t++)
        {
            var target = pos + new Vector3(0f, 0.05f, 0f);  // walk speed
            var result = engine.ResolveWithTransition(
                currentPos:     pos,
                targetPos:      target,
                cellId:         cellId,
                sphereRadius:   0.48f,
                sphereHeight:   1.20f,
                stepUpHeight:   0.60f,
                stepDownHeight: 1.5f,
                isOnGround:     isOnGround,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: DoorEntityId + 1);

            pos    = result.Position;
            cellId = result.CellId;
            isOnGround = result.IsOnGround;
            body.Position = pos;

            if (t % 5 == 0 || result.CollisionNormalValid)
            {
                Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "t={0,2} pos=({1:F3},{2:F3},{3:F3}) cell=0x{4:X8} cnValid={5} cn=({6:F2},{7:F2},{8:F2})",
                    t, pos.X, pos.Y, pos.Z, cellId,
                    result.CollisionNormalValid,
                    result.CollisionNormal.X, result.CollisionNormal.Y, result.CollisionNormal.Z));
            }

            if (pos.Y > 18f) break;
        }

        Console.WriteLine($"Final pos: ({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) cell=0x{cellId:X8}");

        Assert.True(pos.Y < 17.20f,
            $"BUG REPRODUCTION: sphere walked from inside alcove to Y={pos.Y:F3} " +
            $"(past cottage north wall at Y=17.10). Cottage wall should have blocked " +
            $"sphere at Y ≈ 16.62 (wall - sphere reach). If this assertion FAILS, " +
            $"the corner handling at (X=133.5, Y=17.10) is letting sphere slide past.");
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_CottagePolys_NearWalkthroughPosition()
    {
        var fixturePath = Path.Combine(SolutionRoot(),
            "tests", "AcDream.Core.Tests", "Fixtures", "cellar-ascent",
            "0x01000A2B.gfxobj.json");
        var dump    = GfxObjDumpSerializer.Read(fixturePath);
        var physics = GfxObjDumpSerializer.Hydrate(dump);

        var cottagePos = new Vector3(130.5f, 11.5f, 94.0f);
        var cottageRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);

        // Failing sphere position: (133.655, 17.59, 94 .. 95.20)
        // Sphere world AABB: X[133.175, 134.135], Y[17.110, 18.070], Z[94, 95.20]
        var sphereCenterX = 133.655f;
        var sphereCenterY = 17.59f;

        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Cottage GfxObj 0x01000A2B: {0} polys total, BS radius {1:F3}",
            physics.Resolved.Count, physics.BoundingSphere?.Radius ?? 0f));
        Console.WriteLine("Looking for polys whose world-vertex bbox overlaps sphere AABB:");
        Console.WriteLine($"  Sphere X=[{sphereCenterX-0.48f:F3}, {sphereCenterX+0.48f:F3}]");
        Console.WriteLine($"  Sphere Y=[{sphereCenterY-0.48f:F3}, {sphereCenterY+0.48f:F3}]");
        Console.WriteLine($"  Sphere Z=[94.000, 95.200]");

        Console.WriteLine("");
        Console.WriteLine("=== Cottage polys with bbox extending into (X in [130,138], Y in [13,21]) ===");
        int nearXYCount = 0;
        foreach (var (polyId, poly) in physics.Resolved)
        {
            float wxMin = float.MaxValue, wxMax = float.MinValue;
            float wyMin = float.MaxValue, wyMax = float.MinValue;
            float wzMin = float.MaxValue, wzMax = float.MinValue;
            foreach (var v in poly.Vertices)
            {
                var rotated = Vector3.Transform(v, cottageRot);
                var world   = cottagePos + rotated;
                if (world.X < wxMin) wxMin = world.X; if (world.X > wxMax) wxMax = world.X;
                if (world.Y < wyMin) wyMin = world.Y; if (world.Y > wyMax) wyMax = world.Y;
                if (world.Z < wzMin) wzMin = world.Z; if (world.Z > wzMax) wzMax = world.Z;
            }
            // Wide search window.
            if (wxMax < 130 || wxMin > 138) continue;
            if (wyMax < 13  || wyMin > 21)  continue;
            nearXYCount++;
            var nWorld = Vector3.Transform(poly.Plane.Normal, cottageRot);
            Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "  poly 0x{0:X4} n=({1:F2},{2:F2},{3:F2}) X=[{4:F2},{5:F2}] Y=[{6:F2},{7:F2}] Z=[{8:F2},{9:F2}]",
                polyId, nWorld.X, nWorld.Y, nWorld.Z, wxMin, wxMax, wyMin, wyMax, wzMin, wzMax));
        }
        Console.WriteLine($"  Total: {nearXYCount}");

        int matched = 0;
        int matchedXY = 0;
        Console.WriteLine("");
        Console.WriteLine("=== Tight: All cottage polys with XY overlap of sphere AABB (any Z) ===");
        foreach (var (polyId, poly) in physics.Resolved)
        {
            // Transform vertices to world space.
            float wxMin = float.MaxValue, wxMax = float.MinValue;
            float wyMin = float.MaxValue, wyMax = float.MinValue;
            float wzMin = float.MaxValue, wzMax = float.MinValue;
            foreach (var v in poly.Vertices)
            {
                var rotated = Vector3.Transform(v, cottageRot);
                var world   = cottagePos + rotated;
                if (world.X < wxMin) wxMin = world.X; if (world.X > wxMax) wxMax = world.X;
                if (world.Y < wyMin) wyMin = world.Y; if (world.Y > wyMax) wyMax = world.Y;
                if (world.Z < wzMin) wzMin = world.Z; if (world.Z > wzMax) wzMax = world.Z;
            }
            bool xOverlap = wxMax >= sphereCenterX - 0.48f && wxMin <= sphereCenterX + 0.48f;
            bool yOverlap = wyMax >= sphereCenterY - 0.48f && wyMin <= sphereCenterY + 0.48f;
            bool zOverlap = wzMax >= 94f && wzMin <= 95.20f;
            if (xOverlap && yOverlap)
            {
                matchedXY++;
                var nWorld = Vector3.Transform(poly.Plane.Normal, cottageRot);
                string zMark = zOverlap ? " *** Z-OVERLAP ***" : "";
                Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "  poly 0x{0:X4} n=({1:F3},{2:F3},{3:F3}) bbox X=[{4:F3},{5:F3}] Y=[{6:F3},{7:F3}] Z=[{8:F3},{9:F3}]{10}",
                    polyId, nWorld.X, nWorld.Y, nWorld.Z, wxMin, wxMax, wyMin, wyMax, wzMin, wzMax, zMark));
            }
            if (xOverlap && yOverlap && zOverlap) matched++;
        }
        Console.WriteLine($"  XY-overlap polys (any Z): {matchedXY}");
        Console.WriteLine($"  XYZ-overlap polys:        {matched}");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Geometric_DoorSlabAtSphereHeight_OverlapsInZ()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        DatReaderWriter.Types.Frame partFrame;
        float slabLocalZMin = float.MaxValue;
        float slabLocalZMax = float.MinValue;
        using (var dats = new DatCollection(datDir, DatAccessType.Read))
        {
            var setup = dats.Get<DatReaderWriter.DBObjs.Setup>(0x020019FFu)!;
            Assert.NotNull(setup);
            Assert.True(setup.PlacementFrames.ContainsKey(DatReaderWriter.Enums.Placement.Default));
            partFrame = setup.PlacementFrames[DatReaderWriter.Enums.Placement.Default].Frames[0];

            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(DoorGfxObjId)!;
            Assert.NotNull(gfx.PhysicsPolygons);
            // Walk every physics polygon vertex to find local Z extents.
            foreach (var poly in gfx.PhysicsPolygons.Values)
            {
                foreach (ushort vid in poly.VertexIds)
                {
                    if (!gfx.VertexArray.Vertices.TryGetValue(vid, out var sv)) continue;
                    if (sv.Origin.Z < slabLocalZMin) slabLocalZMin = sv.Origin.Z;
                    if (sv.Origin.Z > slabLocalZMax) slabLocalZMax = sv.Origin.Z;
                }
            }
        }

        // Slab local origin shifted up by partFrame.Z. Slab world Z extents:
        float partWorldZ       = DoorSpawnPos.Z + partFrame.Origin.Z;
        float slabWorldZBottom = partWorldZ + slabLocalZMin;
        float slabWorldZTop    = partWorldZ + slabLocalZMax;

        const float SphereHeight = 1.20f;
        const float PlayerFootZ  = 94f;
        float       sphereTopZ   = PlayerFootZ + SphereHeight;

        // The slab IS at sphere height — bottom should be below sphere top.
        Assert.True(slabWorldZBottom < sphereTopZ,
            $"Door slab bottom ({slabWorldZBottom:F3}) should be BELOW " +
            $"player sphere top ({sphereTopZ:F3}). Slab Z range = " +
            $"[{slabWorldZBottom:F3}, {slabWorldZTop:F3}]. Player sphere Z = " +
            $"[{PlayerFootZ:F3}, {sphereTopZ:F3}]. The slab IS at " +
            $"sphere height (overlap from {MathF.Max(slabWorldZBottom, PlayerFootZ):F3} " +
            $"to {MathF.Min(slabWorldZTop, sphereTopZ):F3}). So the inside-out " +
            $"walkthrough is NOT caused by the slab being above the sphere — " +
            $"the bug must be in BSP polygon-level collision response.");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Directional_OutsideIn_SouthApproach_BlocksAtSlabSouthFace()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var (engine, _) = BuildFaithfulDoorEngine(datDir);

        // Sphere starts SOUTH of slab (low Y), moves NORTH (+Y) toward door.
        // Slab world Y ∈ [16.84, 17.10] approximately after 180° entity rot.
        // Sphere south edge needs to be just south of slab south face.
        var currentPos = new Vector3(132.5f, 16.3f, 94f);
        var targetPos  = new Vector3(132.5f, 16.7f, 94f);  // +0.4 m north

        var (result, body) = ResolveAt(engine, currentPos, targetPos, DoorCellOutdoor);

        Assert.True(result.CollisionNormalValid,
            $"Outside-in: door should block sphere. Got: pos={result.Position}, " +
            $"cnValid={result.CollisionNormalValid}, cn={result.CollisionNormal}.");
        Assert.True(result.CollisionNormal.Y < -0.5f,
            $"Outside-in: cn.Y should be negative (south face normal). " +
            $"Got cn={result.CollisionNormal}.");
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Directional_InsideOut_NorthApproach_BlocksAtSlabNorthFace()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var (engine, _) = BuildFaithfulDoorEngine(datDir);

        // Sphere starts NORTH of slab (high Y), moves SOUTH (-Y) toward door.
        var currentPos = new Vector3(132.5f, 17.6f, 94f);
        var targetPos  = new Vector3(132.5f, 17.2f, 94f);  // -0.4 m south

        var (result, body) = ResolveAt(engine, currentPos, targetPos, DoorCellOutdoor);

        Assert.True(result.CollisionNormalValid,
            $"Inside-out: door should block sphere. Got: pos={result.Position}, " +
            $"cnValid={result.CollisionNormalValid}, cn={result.CollisionNormal}.");
        Assert.True(result.CollisionNormal.Y > 0.5f,
            $"Inside-out: cn.Y should be positive (north face normal). " +
            $"Got cn={result.CollisionNormal}.");
    }

    private static (PhysicsEngine engine, PhysicsDataCache cache)
        BuildFaithfulDoorEngine(string datDir)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        DatReaderWriter.DBObjs.Setup setup;
        using (var dats = new DatCollection(datDir, DatAccessType.Read))
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(DoorGfxObjId);
            Assert.NotNull(gfx);
            cache.CacheGfxObj(DoorGfxObjId, gfx!);

            setup = dats.Get<DatReaderWriter.DBObjs.Setup>(0x020019FFu)!;
            Assert.NotNull(setup);
        }

        // Stub landblock at (0, 0) so TryGetLandblockContext succeeds.
        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:  DoorLandblockId,
            terrain:      new TerrainSurface(heights, heightTable),
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        // Build shape list the same way production does
        // (GameWindow.RegisterLiveEntityCollision):
        // 1. ShadowShapeBuilder.FromSetup with entScale=1
        // 2. Substitute BSP shape's radius with the real BoundingSphere.Radius
        var rawShapes = ShadowShapeBuilder.FromSetup(setup, entScale: 1f,
            id => cache.GetGfxObj(id)?.BSP?.Root is not null);
        var shapes = new List<ShadowShape>(rawShapes.Count);
        foreach (var s in rawShapes)
        {
            if (s.CollisionType == ShadowCollisionType.BSP)
            {
                var phys = cache.GetGfxObj(s.GfxObjId);
                float bspR = phys?.BoundingSphere?.Radius ?? 2f;
                shapes.Add(ShadowShape.Bsp(
                    s.GfxObjId,
                    s.LocalPosition,
                    s.LocalRotation,
                    s.Scale,
                    ShadowPartGeometry.Create(
                        new FlatCollisionSphere(Vector3.Zero, bspR / s.Scale),
                        null)));
            }
            else
            {
                shapes.Add(s);
            }
        }
        Assert.Contains(shapes, s => s.CollisionType == ShadowCollisionType.BSP);

        engine.ShadowObjects.RegisterMultiPart(
            entityId:       DoorEntityId,
            entityWorldPos: DoorSpawnPos,
            entityWorldRot: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
            shapes:         shapes,
            state:          DoorClosedState,
            flags:          EntityCollisionFlags.None,
            worldOffsetX:   0f,
            worldOffsetY:   0f,
            landblockId:    DoorLandblockId);

        return (engine, cache);
    }

    private static (ResolveResult result, PhysicsBody body)
        ResolveAt(PhysicsEngine engine, Vector3 currentPos, Vector3 targetPos, uint cellId)
    {
        var body = new PhysicsBody
        {
            Position             = currentPos,
            Orientation          = Quaternion.Identity,
            ContactPlaneValid    = true,
            ContactPlane         = new System.Numerics.Plane(0f, 0f, 1f, -94f),
            ContactPlaneCellId   = cellId,
            WalkablePolygonValid = true,
            WalkablePlane        = new System.Numerics.Plane(0f, 0f, 1f, -94f),
            WalkableVertices     = new[]
            {
                new Vector3(120f, 10f, 94f),
                new Vector3(145f, 10f, 94f),
                new Vector3(145f, 30f, 94f),
                new Vector3(120f, 30f, 94f),
            },
            WalkableUp           = Vector3.UnitZ,
            TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };

        var result = engine.ResolveWithTransition(
            currentPos:     currentPos,
            targetPos:      targetPos,
            cellId:         cellId,
            sphereRadius:   0.48f,
            sphereHeight:   1.20f,
            stepUpHeight:   0.60f,
            stepDownHeight: 1.5f,
            isOnGround:     true,
            body:           body,
            moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: DoorEntityId + 1);

        return (result, body);
    }

    private static readonly Vector3 DoorSpawnPos = new(132.6f, 17.1f, 94.1f);
    private const uint DoorCellOutdoor = 0xA9B40029u;

    [Fact]
    public void AddAllOutsideCells_LandblockLocalSphere_AddsDoorOutdoorCell()
    {
        var sphereWorld = new Vector3(132.3603f, 16.8113f, 94.0000f);
        const float sphereRadius = 0.48f;
        const uint currentCellId = 0xA9B40150u;

        var candidates = new HashSet<uint>();
        CellTransit.AddAllOutsideCells(
            sphereWorld, sphereRadius, currentCellId, Vector3.Zero, candidates);

        const uint expectedDoorCell = 0xA9B40029u;
        Assert.True(candidates.Contains(expectedDoorCell),
            $"AddAllOutsideCells with landblock-local sphere ({sphereWorld.X:F2}, " +
            $"{sphereWorld.Y:F2}, {sphereWorld.Z:F2}) and indoor primary cell " +
            $"0x{currentCellId:X8} should add outdoor cell 0x{expectedDoorCell:X8} " +
            $"(where the door lives). Got: " +
            string.Join(",", candidates.Select(c => $"0x{c:X8}")));
    }

    // ── Engine + door fixture ─────────────────────────────────────────

    private static (PhysicsEngine engine, PhysicsDataCache cache)
        BuildEngineWithDoorFixture(string datDir)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };

        using (var dats = new DatCollection(datDir, DatAccessType.Read))
        {
            var gfx = dats.Get<GfxObj>(DoorGfxObjId);
            Assert.NotNull(gfx);
            Assert.NotNull(gfx!.PhysicsBSP);
            Assert.NotNull(gfx.PhysicsBSP!.Root);
            cache.CacheGfxObj(DoorGfxObjId, gfx);
        }
        Assert.NotNull(cache.GetGfxObj(DoorGfxObjId));

        // 2. Stub landblock so TryGetLandblockContext succeeds at the door XY.
        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f; // far below Z=94
        var stubTerrain = new TerrainSurface(heights, heightTable);
        engine.AddLandblock(
            landblockId:  DoorLandblockId,
            terrain:      stubTerrain,
            cells:        Array.Empty<CellSurface>(),
            portals:      Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);

        var bspShape = ShadowShape.Bsp(
            gfxObjId:      DoorGfxObjId,
            localPosition: Vector3.Zero,
            localRotation: Quaternion.Identity,
            scale:         1f,
            localGeometry:   ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, BspRadius), null));

        var cylShape = ShadowShape.Cylinder(
            gfxObjId:      0u,
            localPosition: CylWorldPos - BspWorldPos, // express cyl relative to entity origin
            localRotation: Quaternion.Identity,
            scale:         1f,
            radius:        CylRadius,
            cylHeight:     CylHeight);

        engine.ShadowObjects.RegisterMultiPart(
            entityId:       DoorEntityId,
            entityWorldPos: BspWorldPos,
            entityWorldRot: Quaternion.Identity,
            shapes:         new[] { cylShape, bspShape },
            state:          DoorClosedState,
            flags:          EntityCollisionFlags.None,
            worldOffsetX:   0f,
            worldOffsetY:   0f,
            landblockId:    DoorLandblockId);

        return (engine, cache);
    }


    private static string? ResolveDatDir()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(datDir) ? datDir : null;
    }

    private static ResolveCaptureRecord LoadCapturedRecord(
        Func<ResolveCaptureRecord, bool> predicate)
    {
        var path = Path.Combine(FixtureDir, "live-capture.jsonl");
        Assert.True(File.Exists(path),
            $"Door-bug live-capture fixture missing: {path}.");

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = System.Text.Json.JsonSerializer
                .Deserialize<ResolveCaptureRecord>(line, CellarUpTrajectoryReplayTests.CaptureJsonOptions)!;
            if (predicate(record))
                return record;
        }

        throw new Xunit.Sdk.XunitException(
            "No captured record matched the predicate. Update the fixture.");
    }

    private static ResolveCaptureRecord LoadOverPenRecord(int index)
    {
        var path = Path.Combine(FixtureDir, "over-penetration-capture.jsonl");
        Assert.True(File.Exists(path),
            $"A6.P5 over-penetration fixture missing: {path}. " +
            $"Run tools/jsonl/extract-records.ps1 to rebuild.");

        var lines = File.ReadAllLines(path);
        Assert.True(lines.Length >= 3,
            $"Expected >= 3 records in {path}; got {lines.Length}");

        var raw = lines[index];
        return System.Text.Json.JsonSerializer
            .Deserialize<ResolveCaptureRecord>(raw,
                CellarUpTrajectoryReplayTests.CaptureJsonOptions)!;
    }

    private static (ResolveResult result, PhysicsBody body) ReplayCapturedCall(
        PhysicsEngine        engine,
        ResolveCaptureRecord captured)
    {
        Assert.NotNull(captured.BodyBefore);
        var body = SeedBodyFromSnapshot(captured.BodyBefore!);

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

        return (result, body);
    }

    private static void AssertCallMatchesCapture(
        PhysicsEngine        engine,
        ResolveCaptureRecord captured)
    {
        Assert.NotNull(captured.BodyBefore);
        Assert.NotNull(captured.BodyAfter);

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

        var divergences = new List<string>();

        AddIfDifferent(divergences, "Result.Position",
            captured.Result.Position, harnessResult.Position);
        AddIfDifferent(divergences, "Result.CellId",
            $"0x{captured.Result.CellId:X8}", $"0x{harnessResult.CellId:X8}");
        AddIfDifferent(divergences, "Result.IsOnGround",
            captured.Result.IsOnGround, harnessResult.IsOnGround);
        AddIfDifferent(divergences, "Result.CollisionNormalValid",
            captured.Result.CollisionNormalValid, harnessResult.CollisionNormalValid);
        if (captured.Result.CollisionNormalValid && harnessResult.CollisionNormalValid)
        {
            AddIfDifferent(divergences, "Result.CollisionNormal",
                captured.Result.CollisionNormal, harnessResult.CollisionNormal);
        }

        AddIfDifferent(divergences, "BodyAfter.Position",
            captured.BodyAfter.Position, body.Position);
        AddIfDifferent(divergences, "BodyAfter.ContactPlaneValid",
            captured.BodyAfter.ContactPlaneValid, body.ContactPlaneValid);
        if (captured.BodyAfter.ContactPlaneValid && body.ContactPlaneValid)
        {
            AddIfDifferent(divergences, "BodyAfter.ContactPlane.Normal",
                captured.BodyAfter.ContactPlane.Normal, body.ContactPlane.Normal);
            AddIfDifferent(divergences, "BodyAfter.ContactPlane.D",
                captured.BodyAfter.ContactPlane.D, body.ContactPlane.D);
        }
        AddIfDifferent(divergences, "BodyAfter.WalkablePolygonValid",
            captured.BodyAfter.WalkablePolygonValid, body.WalkablePolygonValid);
        AddIfDifferent(divergences, "BodyAfter.TransientState",
            $"0x{captured.BodyAfter.TransientState:X}",
            $"0x{(uint)body.TransientState:X}");

        if (divergences.Count > 0)
        {
            string summary = string.Join("\n  * ", divergences);
            string header = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Door-bug harness replay of captured tick {0} diverges from live engine. " +
                "Input: currentPos=({1:F4},{2:F4},{3:F4}) targetPos=({4:F4},{5:F4},{6:F4}) " +
                "cellId=0x{7:X8} isOnGround={8}",
                captured.Tick,
                captured.Input.CurrentPos.X, captured.Input.CurrentPos.Y, captured.Input.CurrentPos.Z,
                captured.Input.TargetPos.X,  captured.Input.TargetPos.Y,  captured.Input.TargetPos.Z,
                captured.Input.CellId, captured.Input.IsOnGround);
            throw new Xunit.Sdk.XunitException(
                header + "\nDivergences (live -> harness):\n  * " + summary);
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
        if (EqualityComparer<T>.Default.Equals(live, harness)) return;
        divergences.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: live={1} harness={2}", name, live, harness));
    }

    private static void AddIfDifferent(
        List<string> divergences, string name, Vector3 live, Vector3 harness)
    {
        if (Vector3.DistanceSquared(live, harness) < 1e-6f) return;
        divergences.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: live=({1:F4},{2:F4},{3:F4}) harness=({4:F4},{5:F4},{6:F4})",
            name, live.X, live.Y, live.Z, harness.X, harness.Y, harness.Z));
    }

    private static void AddIfDifferent(
        List<string> divergences, string name, float live, float harness)
    {
        if (MathF.Abs(live - harness) < 1e-3f) return;
        divergences.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}: live={1:F4} harness={2:F4}", name, live, harness));
    }

    private static string FixtureDir =>
        Path.Combine(SolutionRoot(), "tests", "AcDream.Core.Tests",
                     "Fixtures", "door-bug");

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
