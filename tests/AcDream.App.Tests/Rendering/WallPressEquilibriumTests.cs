using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.Core.Physics;
using AcDream.Core.Rendering;
using DatReaderWriter;
using DatReaderWriter.Options;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Rendering;

[Collection(CameraDiagnosticsCollection.Name)]
[Trait("Purpose", "Diagnostic")]
[Trait("Lane", "InstalledDat")]
public class WallPressEquilibriumTests
{
    private const uint FacilityHubLandblock = 0x8A020000u;

    private readonly ITestOutputHelper _out;
    public WallPressEquilibriumTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveDatDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;
        var def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    private static (PhysicsEngine, PhysicsDataCache) BuildCorridorEngine(DatCollection dats)
    {
        var cache  = new PhysicsDataCache();
        var engine = new PhysicsEngine { DataCache = cache };
        for (uint low = 0x0100u; low <= 0x01FFu; low++)
        {
            uint id = FacilityHubLandblock | low;
            var datCell = dats.Get<DatEnvCell>(id);
            if (datCell is null) continue;
            var environment = dats.Get<DatEnvironment>(0x0D000000u | datCell.EnvironmentId);
            if (environment is null) continue;
            if (!environment.Cells.TryGetValue(datCell.CellStructure, out var cellStruct) || cellStruct is null)
                continue;
            var world = Matrix4x4.CreateFromQuaternion(datCell.Position.Orientation) *
                        Matrix4x4.CreateTranslation(datCell.Position.Origin);
            cache.CacheCellStruct(id, datCell, cellStruct, world);
        }
        var heights     = new byte[81];
        var heightTable = new float[256];
        for (int i = 0; i < 256; i++) heightTable[i] = -1000f;
        engine.AddLandblock(FacilityHubLandblock, new TerrainSurface(heights, heightTable),
            Array.Empty<CellSurface>(), Array.Empty<PortalPlane>(), 0f, 0f);
        return (engine, cache);
    }

    [Fact]
    public void Diagnostic_WallPressedCamera_EyeWanderAndViewerCellStability()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var (engine, _) = BuildCorridorEngine(dats);

        bool  savedAlign = CameraDiagnostics.AlignToSlope;
        bool  savedColl  = CameraDiagnostics.CollideCamera;
        float savedT     = CameraDiagnostics.TranslationStiffness;
        float savedR     = CameraDiagnostics.RotationStiffness;
        try
        {
            CameraDiagnostics.AlignToSlope         = true;
            CameraDiagnostics.CollideCamera        = true;
            CameraDiagnostics.TranslationStiffness = 0.45f;
            CameraDiagnostics.RotationStiffness    = 0.45f;

            var playerPos  = new Vector3(50.331f, -39.357f, -5.90f);
            // Live [resolve]: the sweep target headed (-2.13,+1.32,+0.75) from the
            // pivot and hit the n=(0,-1,0) wall — so the player faces (+X,-Y)-ish
            // and the boom presses -X+Y into that wall. yaw = atan2(-0.53, 0.85).
            float yaw      = -0.556f;
            uint  cellId   = 0x8A020142u;
            float dt       = 1f / 1500f;

            var cam = new RetailChaseCamera
            {
                CollisionProbe = new PhysicsCameraCollisionProbe(engine),
            };

            void Step() => cam.Update(
                playerPosition: playerPos,
                playerYaw: yaw,
                playerVelocity: Vector3.Zero,
                isOnGround: true,
                contactPlaneNormal: Vector3.UnitZ,
                dt: dt,
                cellId: cellId,
                selfEntityId: 0x5);

            // Settle into the wall-press equilibrium.
            for (int i = 0; i < 5000; i++) Step();

            // Measure 20k steady-state frames.
            var eyes = new List<Vector3>(20000);
            var cells = new HashSet<uint>();
            int cellTransitions = 0;
            uint prevCell = cam.ViewerCellId;
            Vector3 prevEye = cam.Position;
            float maxStep = 0f; double sumStep = 0;
            for (int i = 0; i < 20000; i++)
            {
                Step();
                float d = Vector3.Distance(cam.Position, prevEye);
                maxStep = MathF.Max(maxStep, d);
                sumStep += d;
                prevEye = cam.Position;
                eyes.Add(cam.Position);
                cells.Add(cam.ViewerCellId);
                if (cam.ViewerCellId != prevCell) { cellTransitions++; prevCell = cam.ViewerCellId; }
            }

            // Wander bounding box.
            Vector3 mn = eyes[0], mx = eyes[0];
            foreach (var e in eyes) { mn = Vector3.Min(mn, e); mx = Vector3.Max(mx, e); }
            var span = mx - mn;

            _out.WriteLine(FormattableString.Invariant(
                $"steady-state: avgStep={sumStep / 20000 * 1e6:F1}um maxStep={maxStep * 1e6:F1}um wanderBox=({span.X * 1000:F2},{span.Y * 1000:F2},{span.Z * 1000:F2})mm"));
            _out.WriteLine(FormattableString.Invariant(
                $"viewer cells seen: {cells.Count} transitions={cellTransitions} eye=({cam.Position.X:F6},{cam.Position.Y:F6},{cam.Position.Z:F6}) cell=0x{cam.ViewerCellId:X8}"));

            for (int i = 0; i < 16; i++)
            {
                Step();
                _out.WriteLine(FormattableString.Invariant(
                    $"orbit[{i:D2}] eye=({cam.Position.X:F6},{cam.Position.Y:F6},{cam.Position.Z:F6})"));
            }
        }
        finally
        {
            CameraDiagnostics.AlignToSlope         = savedAlign;
            CameraDiagnostics.CollideCamera        = savedColl;
            CameraDiagnostics.TranslationStiffness = savedT;
            CameraDiagnostics.RotationStiffness    = savedR;
        }
    }
}
