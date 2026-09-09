using System;
using System.IO;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class SeamTransitLagTests
{
    private const uint SeamCellWest = 0x8A02016Eu;   // x 75..85
    private const uint SeamCellEast = 0x8A02017Au;   // x 85..88.33

    private readonly ITestOutputHelper _out;
    public SeamTransitLagTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveDatDir()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(datDir) ? datDir : null;
    }

    private static PhysicsEngine BuildEngine(DatCollection dats)
    {
        var engine = new PhysicsEngine();
        engine.DataCache = new PhysicsDataCache();

        var toLoad = new System.Collections.Generic.HashSet<uint> { SeamCellWest, SeamCellEast };
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

    [Theory]
    [InlineData(+1)]   // west → east across x=85
    [InlineData(-1)]   // east → west back across
    public void RunAcrossSeam_CellFlipPosition(int direction)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var engine = BuildEngine(dats);
        var body = GroundedBody();

        const float step = 0.135f;
        float startX = direction > 0 ? 83.8f : 86.2f;
        uint cell = direction > 0 ? SeamCellWest : SeamCellEast;
        var pos = new Vector3(startX, -40f, -6f);

        float? flipX = null;
        for (int tick = 0; tick < 26; tick++)
        {
            var target = pos + new Vector3(direction * step, 0f, 0f);
            var r = engine.ResolveWithTransition(
                currentPos:     pos,
                targetPos:      target,
                cellId:         cell,
                sphereRadius:   0.48f,
                sphereHeight:   1.835f,
                stepUpHeight:   0.4f,
                stepDownHeight: 0.4f,
                isOnGround:     true,
                body:           body,
                moverFlags:     ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide);

            bool flipped = r.CellId != cell;
            _out.WriteLine($"tick={tick,2} pos=({r.Position.X:F3},{r.Position.Y:F3},{r.Position.Z:F3}) " +
                           $"cell=0x{r.CellId:X8} ok={r.Ok}{(flipped ? "  <<< FLIP" : "")}");
            if (flipped && flipX is null)
                flipX = r.Position.X;

            cell = r.CellId;
            pos = r.Position;
            if (direction > 0 && pos.X > 86.4f) break;
            if (direction < 0 && pos.X < 83.6f) break;
        }

        Assert.NotNull(flipX);
        float lag = direction > 0 ? flipX!.Value - 85.00f : 85.00f - flipX!.Value;
        _out.WriteLine($"flip at x={flipX:F3} → lag past the plane = {lag:F3} m " +
                       $"(one-tick quantization bound = {step:F3} m)");
        // Diagnostic, not a pin: the finding is the printed lag. A lag beyond
        // one tick step is the divergence under investigation.
    }
}
