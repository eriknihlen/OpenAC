using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;
using Plane = System.Numerics.Plane;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class DoorCollisionApparatusTests
{
    private readonly ITestOutputHelper _out;
    public DoorCollisionApparatusTests(ITestOutputHelper output) => _out = output;

    private const uint DoorSetupId = 0x020019FFu;
    private const uint DoorFrameGfxObjId = 0x010044B5u;
    private const uint DoorEntityId = 0xF424Fu;
    private const uint TestLandblockId = 0xA9B40000u;

    private const uint TestCellId = TestLandblockId | 0x0001u;

    private const float SphereRadius   = 0.48f;
    private const float SphereHeight   = 1.20f;
    private const float StepUpHeight   = 0.60f;
    private const float StepDownHeight = 0.04f;

    [Fact]
    public void Apparatus_DeadCenter_FrontApproach_BlocksOnBSP()
    {
        if (!TryBuildScenario(out var ctx)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        // Door at world (12, 12, 0). The frame's BSP slab extends
        // entity-local Y∈[-0.009, 0.252] (poly Y range plus frame[0] offset).
        // Approach from -Y at sphere Y=11 (1 m before the front face),
        // walk +Y at 0.10 m/tick.
        var start = new Vector3(12f, 11f, 0.5f);
        var perTick = new Vector3(0f, 0.10f, 0f);

        var (blocked, finalPos, normal, ticks) = SweepUntilBlocked(
            ctx.engine, start, perTick, maxTicks: 30);

        _out.WriteLine($"Final pos = ({finalPos.X:F3}, {finalPos.Y:F3}, {finalPos.Z:F3}) " +
                       $"after {ticks} ticks; blocked={blocked} normal=({normal.X:F3},{normal.Y:F3},{normal.Z:F3})");

        Assert.True(blocked,
            $"Expected collision before passing through the door. Sphere reached " +
            $"({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}) after {ticks} ticks " +
            $"without firing CollisionNormalValid.");
        Assert.True(finalPos.Y < 12.0f,
            $"Sphere should stop before the door's front face (Y ≈ 11.99). " +
            $"Got Y={finalPos.Y:F3}");
    }

    [Fact]
    public void Apparatus_SingleLargeTickJump_DeadCenter_StillBlocksOnBSP()
    {
        if (!TryBuildScenario(out var ctx)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var start = new Vector3(12f, 11f, 0.5f);
        var target = new Vector3(12f, 13f, 0.5f);

        var result = ctx.engine.ResolveWithTransition(
            start, target, TestCellId,
            SphereRadius, SphereHeight, StepUpHeight, StepDownHeight,
            isOnGround: false, body: null,
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: 0);

        _out.WriteLine(
            $"single-jump pos=({result.Position.X:F3},{result.Position.Y:F3},{result.Position.Z:F3}) "
            + $"hit={result.CollisionNormalValid} "
            + $"normal=({result.CollisionNormal.X:F3},{result.CollisionNormal.Y:F3},{result.CollisionNormal.Z:F3})");

        Assert.True(result.CollisionNormalValid,
            "A single large-tick jump through the door's BSP slab must still "
            + "report a collision, matching the 30-tick sweep's finding. If "
            + "this fails, candidate (a) (the sweep misses large single-tick "
            + "deltas) is CONFIRMED against real wall geometry.");
        Assert.True(result.Position.Y < 12.0f,
            $"Sphere should stop before the door's front face (Y ≈ 11.99) even "
            + $"on a single large-tick jump; got Y={result.Position.Y:F3}");
    }

    [Fact]
    public void Apparatus_50cmOffCenter_FrontApproach_BlocksOnBSP()
    {
        if (!TryBuildScenario(out var ctx)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var start = new Vector3(12.5f, 11f, 0.5f);
        var perTick = new Vector3(0f, 0.10f, 0f);

        var (blocked, finalPos, normal, ticks) = SweepUntilBlocked(
            ctx.engine, start, perTick, maxTicks: 30);

        _out.WriteLine($"Final pos = ({finalPos.X:F3}, {finalPos.Y:F3}, {finalPos.Z:F3}) " +
                       $"after {ticks} ticks; blocked={blocked} normal=({normal.X:F3},{normal.Y:F3},{normal.Z:F3})");

        Assert.True(blocked,
            $"Expected BSP collision off-center. Sphere reached " +
            $"({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}) after {ticks} ticks " +
            $"without firing CollisionNormalValid.");
        Assert.True(finalPos.Y < 12.0f,
            $"Sphere should stop before door front face. Got Y={finalPos.Y:F3}");
    }

    /// <summary>
    /// Reverse approach: walk from the door's +Y back side toward -Y.
    /// SidesType=Landblock polys are two-sided, so this must also block.
    /// This pins the live "walking out from inside passes through" bug
    /// in its simplest geometric form.
    /// </summary>
    [Fact]
    public void Apparatus_DeadCenter_BackApproach_BlocksOnBSP()
    {
        if (!TryBuildScenario(out var ctx)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        // Start past the door at Y=13, walk back toward -Y.
        var start = new Vector3(12f, 13f, 0.5f);
        var perTick = new Vector3(0f, -0.10f, 0f);

        var (blocked, finalPos, normal, ticks) = SweepUntilBlocked(
            ctx.engine, start, perTick, maxTicks: 30);

        _out.WriteLine($"Final pos = ({finalPos.X:F3}, {finalPos.Y:F3}, {finalPos.Z:F3}) " +
                       $"after {ticks} ticks; blocked={blocked} normal=({normal.X:F3},{normal.Y:F3},{normal.Z:F3})");

        Assert.True(blocked,
            $"Expected BSP collision from back side (two-sided Landblock poly). " +
            $"Sphere reached ({finalPos.X:F3},{finalPos.Y:F3},{finalPos.Z:F3}) " +
            $"after {ticks} ticks.");
        Assert.True(finalPos.Y > 12.30f,
            $"Sphere should stop after the door's back face (Y ≈ 12.25). " +
            $"Got Y={finalPos.Y:F3}");
    }

    /// <summary>
    /// Diagnostic dump: drive 5 ticks with probes on, just to see
    /// what the engine reports per-tick. Useful when the assertion
    /// tests above fail — the diagnostic test shows the FULL log
    /// without yielding a green/red verdict.
    /// </summary>
    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Apparatus_DiagnosticDump_FrontApproach()
    {
        if (!TryBuildScenario(out var ctx)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        PhysicsDiagnostics.ProbeResolveEnabled    = true;
        PhysicsDiagnostics.ProbeBuildingEnabled   = true;
        try
        {
            var pos = new Vector3(12f, 11f, 0.5f);
            var perTick = new Vector3(0f, 0.10f, 0f);
            uint cellId = TestCellId;
            bool isOnGround = false;

            for (int tick = 0; tick < 8; tick++)
            {
                Vector3 target = pos + perTick;
                var result = ctx.engine.ResolveWithTransition(
                    pos, target, cellId,
                    SphereRadius, SphereHeight,
                    StepUpHeight, StepDownHeight,
                    isOnGround,
                    body: null,
                    moverFlags: ObjectInfoState.IsPlayer,
                    movingEntityId: 0);
                _out.WriteLine($"tick={tick} pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) → " +
                               $"({result.Position.X:F3},{result.Position.Y:F3},{result.Position.Z:F3}) " +
                               $"hit={result.CollisionNormalValid} " +
                               $"normal=({result.CollisionNormal.X:F3},{result.CollisionNormal.Y:F3},{result.CollisionNormal.Z:F3})");
                pos = result.Position;
                cellId = result.CellId;
                isOnGround = result.IsOnGround;
            }

        }
        finally
        {
            PhysicsDiagnostics.ProbeResolveEnabled    = false;
            PhysicsDiagnostics.ProbeBuildingEnabled   = false;
        }
    }

    [Fact]
    public void Apparatus_Grounded_50cmOffCenter_FrontApproach_Blocks()
    {
        if (!TryBuildScenario(out var ctx)) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        PhysicsDiagnostics.ProbeResolveEnabled  = true;
        PhysicsDiagnostics.ProbeBuildingEnabled = true;

        var floorPlane = new Plane(0f, 0f, 1f, 0f); // z = 0 plane
        var floorVerts = new[]
        {
            new Vector3( 0f,  0f, 0f),
            new Vector3(24f,  0f, 0f),
            new Vector3(24f, 24f, 0f),
            new Vector3( 0f, 24f, 0f),
        };

        var body = new PhysicsBody
        {
            Position             = new Vector3(12.5f, 11f, 0.48f),
            Orientation          = Quaternion.Identity,
            ContactPlaneValid    = true,
            ContactPlane         = floorPlane,
            ContactPlaneCellId   = TestCellId,
            WalkablePolygonValid = true,
            WalkablePlane        = floorPlane,
            WalkableVertices     = floorVerts,
            WalkableUp           = Vector3.UnitZ,
            TransientState       = TransientStateFlags.Contact | TransientStateFlags.OnWalkable,
        };

        var perTick = new Vector3(0f, 0.10f, 0f);
        Vector3 pos = body.Position;
        uint cellId = TestCellId;
        bool isOnGround = true;
        bool blocked = false;
        Vector3 lastNormal = Vector3.Zero;
        int ticks = 0;

        for (int tick = 0; tick < 30; tick++)
        {
            Vector3 target = pos + perTick;
            var result = ctx.engine.ResolveWithTransition(
                pos, target, cellId,
                SphereRadius, SphereHeight,
                StepUpHeight, StepDownHeight,
                isOnGround,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            ticks = tick;
            body.Position = result.Position;
            pos = result.Position;
            cellId = result.CellId;
            isOnGround = result.IsOnGround;
            lastNormal = result.CollisionNormal;

            if (result.CollisionNormalValid)
            {
                blocked = true;
                _out.WriteLine($"Tick {tick}: BLOCKED at pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) normal=({lastNormal.X:F3},{lastNormal.Y:F3},{lastNormal.Z:F3})");
                break;
            }
        }

        _out.WriteLine($"Final pos = ({pos.X:F3}, {pos.Y:F3}, {pos.Z:F3}) after {ticks + 1} ticks; blocked={blocked}");
        _out.WriteLine($"Grounded={isOnGround}");

        PhysicsDiagnostics.ProbeResolveEnabled  = false;
        PhysicsDiagnostics.ProbeBuildingEnabled = false;

        Assert.True(blocked,
            $"Door must block the grounded off-center approach. " +
            $"Current pos=({pos.X:F3},{pos.Y:F3},{pos.Z:F3}) blocked={blocked}");
        Assert.True(pos.Y < 12.0f,
            $"Sphere must stop before the slab; pos.Y={pos.Y:F3}");
    }

    // ───────────────────────────────────────────────────────────────
    // Apparatus setup
    // ───────────────────────────────────────────────────────────────

    private sealed record Context(
        PhysicsEngine engine,
        PhysicsDataCache cache,
        Setup setup,
        IReadOnlyList<ShadowShape> registeredShapes);

    private bool TryBuildScenario(out Context ctx)
    {
        ctx = default!;
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            return false;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var doorFrameGfx = dats.Get<GfxObj>(DoorFrameGfxObjId);
        Assert.NotNull(doorFrameGfx);
        var cache = new PhysicsDataCache();
        cache.CacheGfxObj(DoorFrameGfxObjId, doorFrameGfx!);
        Assert.NotNull(cache.GetGfxObj(DoorFrameGfxObjId));

        var engine = new PhysicsEngine { DataCache = cache };

        var heights = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(
            landblockId:    TestLandblockId,
            terrain:        new TerrainSurface(heights, heightTable),
            cells:          Array.Empty<CellSurface>(),
            portals:        Array.Empty<PortalPlane>(),
            worldOffsetX:   0f,
            worldOffsetY:   0f);

        // Load door setup, build shapes via the production builder.
        var setup = dats.Get<Setup>(DoorSetupId);
        Assert.NotNull(setup);
        var shapes = ShadowShapeBuilder.FromSetup(
            setup!,
            entScale: 1.0f,
            hasPhysicsBsp: id => cache.GetGfxObj(id)?.BSP?.Root is not null);

        _out.WriteLine($"Shapes from FromSetup: {shapes.Count} entries");
        foreach (var s in shapes)
        {
            _out.WriteLine($"  type={s.CollisionType} gfxObj=0x{s.GfxObjId:X8} " +
                           $"localPos=({s.LocalPosition.X:F3},{s.LocalPosition.Y:F3},{s.LocalPosition.Z:F3}) " +
                           $"radius={s.Radius:F3} cylH={s.CylHeight:F3}");
        }

        Vector3 doorWorldPos = new(12f, 12f, 0f);
        Quaternion doorWorldRot = Quaternion.Identity;
        const uint doorState = 0x10008u; // PhysicsState.HasPhysicsBSP | ReportCollisions

        engine.ShadowObjects.RegisterMultiPart(
            entityId:        DoorEntityId,
            entityWorldPos:  doorWorldPos,
            entityWorldRot:  doorWorldRot,
            shapes:          shapes,
            state:           doorState,
            flags:           EntityCollisionFlags.None,
            worldOffsetX:    0f,
            worldOffsetY:    0f,
            landblockId:     TestLandblockId);

        ctx = new Context(engine, cache, setup!, shapes);
        return true;
    }

    private (bool blocked, Vector3 finalPos, Vector3 normal, int ticks)
        SweepUntilBlocked(PhysicsEngine engine, Vector3 start, Vector3 perTick, int maxTicks)
    {
        Vector3 pos = start;
        uint cellId = TestCellId;
        bool isOnGround = false;
        Vector3 lastNormal = Vector3.Zero;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            Vector3 target = pos + perTick;
            var result = engine.ResolveWithTransition(
                pos, target, cellId,
                SphereRadius, SphereHeight,
                StepUpHeight, StepDownHeight,
                isOnGround,
                body: null,
                moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
                movingEntityId: 0);

            if (result.CollisionNormalValid)
                return (true, result.Position, result.CollisionNormal, tick);

            pos = result.Position;
            cellId = result.CellId;
            isOnGround = result.IsOnGround;
            lastNormal = result.CollisionNormal;
        }

        return (false, pos, lastNormal, maxTicks);
    }
}
