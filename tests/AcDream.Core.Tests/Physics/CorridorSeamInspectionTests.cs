using System;
using System.IO;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class CorridorSeamInspectionTests
{
    private readonly ITestOutputHelper _out;
    public CorridorSeamInspectionTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(0x8A02016Eu)]
    [InlineData(0x8A02017Au)]
    [InlineData(0x8A02011Eu)]
    [InlineData(0x8A020179u)]
    [InlineData(0x8A02017Eu)]
    public void CorridorCell_PhysicsPolysAndPortals_DatInspection(uint envCellId)
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var envCell = dats.Get<EnvCell>(envCellId);
        Assert.NotNull(envCell);
        _out.WriteLine($"=== EnvCell 0x{envCellId:X8} ===");
        _out.WriteLine($"  pos=({envCell!.Position.Origin.X:F2},{envCell.Position.Origin.Y:F2},{envCell.Position.Origin.Z:F2}) " +
                       $"rot=({envCell.Position.Orientation.X:F3},{envCell.Position.Orientation.Y:F3},{envCell.Position.Orientation.Z:F3},{envCell.Position.Orientation.W:F3})");
        _out.WriteLine($"  EnvironmentId=0x{envCell.EnvironmentId:X4} CellStructure={envCell.CellStructure}");
        _out.WriteLine($"  CellPortals={envCell.CellPortals.Count}");
        foreach (var p in envCell.CellPortals)
            _out.WriteLine($"    portal poly={p.PolygonId} other=0x{p.OtherCellId:X4} flags={p.Flags}");

        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell.EnvironmentId);
        Assert.NotNull(environment);
        Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cs));

        _out.WriteLine($"  PhysicsPolygons={cs!.PhysicsPolygons.Count} (portal-relevant normals below)");
        foreach (var (id, poly) in cs.PhysicsPolygons)
        {
            var verts = poly.VertexIds;
            if (verts.Count < 3) continue;
            if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[0], out var v0)) continue;
            if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[1], out var v1)) continue;
            if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[2], out var v2)) continue;
            var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(
                v1.Origin - v0.Origin, v2.Origin - v0.Origin));

            if (MathF.Abs(n.Z) > 0.3f) continue;
            _out.WriteLine($"    poly {id}: n=({n.X:F2},{n.Y:F2},{n.Z:F2}) v0=({v0.Origin.X:F2},{v0.Origin.Y:F2},{v0.Origin.Z:F2}) verts={verts.Count} sides={poly.SidesType} stip={poly.Stippling}");
        }

        // The portal polygons live in the VISUAL polygon set — print their
        // ids so overlap with the physics set (same id space?) is visible.
        _out.WriteLine($"  VisualPolygons={cs.Polygons.Count}");
        foreach (var p in envCell.CellPortals)
        {
            if (cs.Polygons.TryGetValue((ushort)p.PolygonId, out var vp))
            {
                _out.WriteLine($"    portal-poly {p.PolygonId} IS in the visual set (verts={vp.VertexIds.Count})");
                bool inPhysics = cs.PhysicsPolygons.ContainsKey((ushort)p.PolygonId);
                _out.WriteLine($"    portal-poly {p.PolygonId} in PHYSICS set: {inPhysics}");
            }
        }
    }

    [Theory]
    [InlineData(0x8A02016Eu)]
    [InlineData(0x8A02017Au)]
    public void CorridorCell_PhysicsBspLeafMembership_OfPortalPolys(uint envCellId)
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var envCell = dats.Get<EnvCell>(envCellId);
        Assert.NotNull(envCell);
        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell!.EnvironmentId);
        Assert.NotNull(environment);
        Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cs));

        var portalPolyIds = new System.Collections.Generic.HashSet<ushort>();
        foreach (var p in envCell.CellPortals)
            portalPolyIds.Add((ushort)p.PolygonId);

        _out.WriteLine($"=== EnvCell 0x{envCellId:X8} — physics BSP leaf membership ===");
        _out.WriteLine($"  Env=0x{envCell.EnvironmentId:X4} struct={envCell.CellStructure} " +
                       $"portalPolyIds=[{string.Join(",", portalPolyIds)}] " +
                       $"physicsTable=[{string.Join(",", cs!.PhysicsPolygons.Keys)}]");

        var root = cs.PhysicsBSP?.Root;
        Assert.NotNull(root);

        int leafCount = 0;
        var leafPolyIds = new System.Collections.Generic.HashSet<ushort>();
        var portalPolyLeafHits = new System.Collections.Generic.List<string>();
        var stack = new System.Collections.Generic.Stack<(DatReaderWriter.Types.PhysicsBSPNode Node, string Path)>();
        stack.Push((root!, "R"));
        while (stack.Count > 0)
        {
            var (n, path) = stack.Pop();
            if (n.Polygons is { Count: > 0 })
            {
                leafCount++;
                foreach (var pid in n.Polygons)
                {
                    leafPolyIds.Add(pid);
                    if (portalPolyIds.Contains(pid))
                        portalPolyLeafHits.Add($"poly {pid} in leaf@{path} (type={n.Type}, polys=[{string.Join(",", n.Polygons)}])");
                }
            }
            if (n.PosNode is not null) stack.Push((n.PosNode, path + "+"));
            if (n.NegNode is not null) stack.Push((n.NegNode, path + "-"));
        }

        _out.WriteLine($"  BSP leaves-with-polys={leafCount} distinctLeafPolyIds=[{string.Join(",", leafPolyIds)}]");
        var tableNotInLeaves = new System.Collections.Generic.List<ushort>();
        foreach (var pid in cs.PhysicsPolygons.Keys)
            if (!leafPolyIds.Contains(pid))
                tableNotInLeaves.Add(pid);
        _out.WriteLine($"  physics-table polys NOT referenced by any BSP leaf: [{string.Join(",", tableNotInLeaves)}]");

        if (portalPolyLeafHits.Count == 0)
        {
            _out.WriteLine("  >>> NO portal polygon is referenced by any physics-BSP leaf — " +
                           "retail's sphere_intersects_poly never tests them from this cell's BSP.");
        }
        else
        {
            foreach (var hit in portalPolyLeafHits)
                _out.WriteLine($"  >>> PORTAL POLY IN PHYSICS LEAF: {hit}");
        }
    }

    [Fact]
    public void HumanSetup_CollisionSpheres_DatTruth()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var setup = dats.Get<DatReaderWriter.DBObjs.Setup>(0x02000001u);
        Assert.NotNull(setup);

        _out.WriteLine($"Setup 0x02000001: Height={setup!.Height:F3} Radius={setup.Radius:F3} " +
                       $"StepUp={setup.StepUpHeight:F3} StepDown={setup.StepDownHeight:F3}");
        _out.WriteLine($"Spheres ({setup.Spheres.Count}):");
        foreach (var s in setup.Spheres)
            _out.WriteLine($"  origin=({s.Origin.X:F3},{s.Origin.Y:F3},{s.Origin.Z:F3}) r={s.Radius:F3}");
        _out.WriteLine($"CylSpheres ({setup.CylSpheres.Count}):");
        foreach (var c in setup.CylSpheres)
            _out.WriteLine($"  origin=({c.Origin.X:F3},{c.Origin.Y:F3},{c.Origin.Z:F3}) r={c.Radius:F3} h={c.Height:F3}");
    }

    [Fact]
    public void WindowShaft_FullPolyDump()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (var cellId in new[] { 0x8A02017Eu, 0x8A020179u })
        {
            var envCell = dats.Get<EnvCell>(cellId);
            Assert.NotNull(envCell);
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell!.EnvironmentId);
            Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cs));

            var rot = new System.Numerics.Quaternion(
                envCell.Position.Orientation.X, envCell.Position.Orientation.Y,
                envCell.Position.Orientation.Z, envCell.Position.Orientation.W);
            var world = System.Numerics.Matrix4x4.CreateFromQuaternion(rot)
                      * System.Numerics.Matrix4x4.CreateTranslation(
                            envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);

            _out.WriteLine($"=== 0x{cellId:X8} full physics polys (world verts) ===");
            foreach (var (id, poly) in cs!.PhysicsPolygons)
            {
                var verts = poly.VertexIds;
                if (verts.Count < 3) continue;
                var w = new System.Collections.Generic.List<System.Numerics.Vector3>();
                foreach (var vid in verts)
                    if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v))
                        w.Add(System.Numerics.Vector3.Transform(v.Origin, world));
                var n = System.Numerics.Vector3.Normalize(
                    System.Numerics.Vector3.Cross(w[1] - w[0], w[2] - w[0]));

                if (cellId == 0x8A020179u && MathF.Abs(n.Y) < 0.3f && n.Z > -0.3f) continue;

                var vs = string.Join(" ", w.ConvertAll(p => $"({p.X:F2},{p.Y:F2},{p.Z:F2})"));
                _out.WriteLine($"  poly {id}: n=({n.X:F2},{n.Y:F2},{n.Z:F2}) verts={vs}");
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void CorridorSeam_FindPolygonMatchingLiveHit()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        // Live evidence (launch-175-verify2.log:42858).
        var hitPoint  = new System.Numerics.Vector3(85.253f, -39.776f, -5.992f);
        var hitNormal = new System.Numerics.Vector3(-1.00f, 0.03f, -0.03f);
        hitNormal = System.Numerics.Vector3.Normalize(hitNormal);
        const float sphereRadius = 0.48f;

        var cellIds = new System.Collections.Generic.HashSet<uint>
        {
            0x8A02016Eu, 0x8A02017Au,
        };
        foreach (var seed in new[] { 0x8A02016Eu, 0x8A02017Au })
        {
            var seedCell = dats.Get<EnvCell>(seed);
            if (seedCell is null) continue;
            foreach (var p in seedCell.CellPortals)
                cellIds.Add(0x8A020000u | p.OtherCellId);
        }

        foreach (var cellId in cellIds)
        {
            var envCell = dats.Get<EnvCell>(cellId);
            if (envCell is null) { _out.WriteLine($"cell 0x{cellId:X8}: NOT FOUND"); continue; }
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell.EnvironmentId);
            if (environment is null || !environment.Cells.TryGetValue(envCell.CellStructure, out var cs))
                continue;

            var rot = new System.Numerics.Quaternion(
                envCell.Position.Orientation.X, envCell.Position.Orientation.Y,
                envCell.Position.Orientation.Z, envCell.Position.Orientation.W);
            var world = System.Numerics.Matrix4x4.CreateFromQuaternion(rot)
                      * System.Numerics.Matrix4x4.CreateTranslation(
                            envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);

            var portalPolyIds = new System.Collections.Generic.HashSet<ushort>();
            foreach (var p in envCell.CellPortals) portalPolyIds.Add((ushort)p.PolygonId);

            foreach (var (id, poly) in cs!.PhysicsPolygons)
            {
                var verts = poly.VertexIds;
                if (verts.Count < 3) continue;
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[0], out var v0)) continue;
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[1], out var v1)) continue;
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[2], out var v2)) continue;

                var w0 = System.Numerics.Vector3.Transform(v0.Origin, world);
                var w1 = System.Numerics.Vector3.Transform(v1.Origin, world);
                var w2 = System.Numerics.Vector3.Transform(v2.Origin, world);
                var n  = System.Numerics.Vector3.Normalize(
                    System.Numerics.Vector3.Cross(w1 - w0, w2 - w0));

                float align = System.Numerics.Vector3.Dot(n, hitNormal);
                if (MathF.Abs(align) < 0.95f) continue;   // within ~18° of the recorded normal

                // Plane distance from the hit point.
                float d = -System.Numerics.Vector3.Dot(n, w0);
                float dist = System.Numerics.Vector3.Dot(n, hitPoint) + d;
                if (MathF.Abs(dist) > sphereRadius + 0.1f) continue;

                // Rough proximity: hit point near the polygon's vertex span.
                float minX = MathF.Min(w0.X, MathF.Min(w1.X, w2.X)) - 1f;
                float maxX = MathF.Max(w0.X, MathF.Max(w1.X, w2.X)) + 1f;
                float minY = MathF.Min(w0.Y, MathF.Min(w1.Y, w2.Y)) - 1f;
                float maxY = MathF.Max(w0.Y, MathF.Max(w1.Y, w2.Y)) + 1f;
                if (hitPoint.X < minX || hitPoint.X > maxX ||
                    hitPoint.Y < minY || hitPoint.Y > maxY) continue;

                _out.WriteLine(
                    $">>> CANDIDATE cell=0x{cellId:X8} poly={id} " +
                    $"worldN=({n.X:F3},{n.Y:F3},{n.Z:F3}) align={align:F3} planeDist={dist:F3} " +
                    $"isPortalPoly={portalPolyIds.Contains(id)} " +
                    $"w0=({w0.X:F2},{w0.Y:F2},{w0.Z:F2}) w1=({w1.X:F2},{w1.Y:F2},{w1.Z:F2}) w2=({w2.X:F2},{w2.Y:F2},{w2.Z:F2}) " +
                    $"verts={verts.Count} sides={poly.SidesType} stip={poly.Stippling}");
            }
        }
        _out.WriteLine("(sweep complete)");
    }

    [Fact]
    public void CorridorSeam_DownwardPolysNearSeam()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (var cellId in new[] { 0x8A02016Eu, 0x8A02017Au })
        {
            var envCell = dats.Get<EnvCell>(cellId);
            Assert.NotNull(envCell);
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell!.EnvironmentId);
            Assert.NotNull(environment);
            Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cs));

            var rot = new System.Numerics.Quaternion(
                envCell.Position.Orientation.X, envCell.Position.Orientation.Y,
                envCell.Position.Orientation.Z, envCell.Position.Orientation.W);
            var world = System.Numerics.Matrix4x4.CreateFromQuaternion(rot)
                      * System.Numerics.Matrix4x4.CreateTranslation(
                            envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);

            _out.WriteLine($"=== 0x{cellId:X8} downward physics polys (n.Z < -0.3) ===");
            foreach (var (id, poly) in cs!.PhysicsPolygons)
            {
                var verts = poly.VertexIds;
                if (verts.Count < 3) continue;
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[0], out var v0)) continue;
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[1], out var v1)) continue;
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)verts[2], out var v2)) continue;

                var w0 = System.Numerics.Vector3.Transform(v0.Origin, world);
                var w1 = System.Numerics.Vector3.Transform(v1.Origin, world);
                var w2 = System.Numerics.Vector3.Transform(v2.Origin, world);
                var n  = System.Numerics.Vector3.Normalize(
                    System.Numerics.Vector3.Cross(w1 - w0, w2 - w0));
                if (n.Z > -0.3f) continue;

                float minX = MathF.Min(w0.X, MathF.Min(w1.X, w2.X));
                float maxX = MathF.Max(w0.X, MathF.Max(w1.X, w2.X));
                if (maxX < 83.5f || minX > 87.0f) continue;

                var allW = new System.Collections.Generic.List<System.Numerics.Vector3>();
                foreach (var vid in verts)
                    if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var vv))
                        allW.Add(System.Numerics.Vector3.Transform(vv.Origin, world));
                float minZ = float.MaxValue, maxZ = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var w in allW)
                {
                    minZ = MathF.Min(minZ, w.Z); maxZ = MathF.Max(maxZ, w.Z);
                    minY = MathF.Min(minY, w.Y); maxY = MathF.Max(maxY, w.Y);
                }

                _out.WriteLine(
                    $"  poly {id}: worldN=({n.X:F2},{n.Y:F2},{n.Z:F2}) x=[{minX:F2},{maxX:F2}] " +
                    $"y=[{minY:F2},{maxY:F2}] z=[{minZ:F2},{maxZ:F2}] verts={verts.Count} " +
                    $"sides={poly.SidesType} stip={poly.Stippling}");
            }
        }
        _out.WriteLine("(downward sweep complete)");
    }

    [Theory]
    [InlineData(0x8A02016Eu)]
    [InlineData(0x8A02017Au)]
    public void CorridorCell_PortalPolygonWorldSpans(uint envCellId)
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var envCell = dats.Get<EnvCell>(envCellId);
        Assert.NotNull(envCell);
        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell!.EnvironmentId);
        Assert.NotNull(environment);
        Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cs));

        var rot = new System.Numerics.Quaternion(
            envCell.Position.Orientation.X, envCell.Position.Orientation.Y,
            envCell.Position.Orientation.Z, envCell.Position.Orientation.W);
        var world = System.Numerics.Matrix4x4.CreateFromQuaternion(rot)
                  * System.Numerics.Matrix4x4.CreateTranslation(
                        envCell.Position.Origin.X, envCell.Position.Origin.Y, envCell.Position.Origin.Z);

        _out.WriteLine($"=== 0x{envCellId:X8} portal polygons (world spans) ===");
        foreach (var p in envCell.CellPortals)
        {
            if (!cs!.Polygons.TryGetValue((ushort)p.PolygonId, out var poly))
            {
                _out.WriteLine($"  portal poly {p.PolygonId} -> 0x{p.OtherCellId:X4} {p.Flags}: NOT in visual set");
                continue;
            }

            var min = new System.Numerics.Vector3(float.MaxValue);
            var max = new System.Numerics.Vector3(float.MinValue);
            foreach (var vid in poly.VertexIds)
            {
                if (!cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) continue;
                var w = System.Numerics.Vector3.Transform(v.Origin, world);
                min = System.Numerics.Vector3.Min(min, w);
                max = System.Numerics.Vector3.Max(max, w);
            }
            _out.WriteLine(
                $"  portal poly {p.PolygonId} -> 0x{p.OtherCellId:X4} [{p.Flags}] " +
                $"x=[{min.X:F2},{max.X:F2}] y=[{min.Y:F2},{max.Y:F2}] z=[{min.Z:F2},{max.Z:F2}] " +
                $"verts={poly.VertexIds.Count}");
        }
    }
}
