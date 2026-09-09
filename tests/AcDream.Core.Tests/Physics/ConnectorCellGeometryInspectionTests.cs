using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public class ConnectorCellGeometryInspectionTests
{
    private readonly ITestOutputHelper _out;
    public ConnectorCellGeometryInspectionTests(ITestOutputHelper output) => _out = output;

    private static string? DatDir()
    {
        var d = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile), "Documents", "Asheron's Call");
        return Directory.Exists(d) ? d : null;
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Dump_ConnectorCells_ShellAndCollision()
    {
        var datDir = DatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint cellId in new[] { 0xF6820116u, 0xF6820117u, 0xF6820118u })
        {
            var env = dats.Get<EnvCell>(cellId);
            if (env is null) { _out.WriteLine($"cell 0x{cellId:X8}: EnvCell NOT FOUND"); continue; }

            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | env.EnvironmentId);
            environment!.Cells.TryGetValue(env.CellStructure, out var cs);

            var portals = env.CellPortals?.Select(p => $"0x{(0xF6820000u | p.OtherCellId):X8}") ?? Enumerable.Empty<string>();
            _out.WriteLine($"=== cell 0x{cellId:X8}  envId=0x{env.EnvironmentId:X4} struct={env.CellStructure} " +
                           $"pos=({env.Position.Origin.X:F2},{env.Position.Origin.Y:F2},{env.Position.Origin.Z:F2}) ===");
            _out.WriteLine($"   CellPortals -> [{string.Join(",", portals)}]   VisibleCells={env.VisibleCells?.Count ?? 0}");

            if (cs is null) { _out.WriteLine("   CellStruct: NULL"); continue; }

            int renderPolys = cs.Polygons?.Count ?? 0;
            int physPolys    = cs.PhysicsPolygons?.Count ?? 0;
            bool physBsp     = cs.PhysicsBSP?.Root is not null;
            bool cellBsp     = cs.CellBSP?.Root is not null;
            _out.WriteLine($"   renderPolys={renderPolys}  physicsPolys={physPolys}  physicsBSP={(physBsp ? "YES" : "NO")}  cellBSP={(cellBsp ? "YES" : "NO")}");

            if (cs.Polygons is not null && cs.VertexArray?.Vertices is not null)
            {
                var buckets = new Dictionary<string, int>();
                foreach (var poly in cs.Polygons.Values)
                {
                    var n = PolyNormal(poly, cs.VertexArray);
                    if (n is null) continue;
                    buckets.TryGetValue(DomAxis(n.Value), out int c);
                    buckets[DomAxis(n.Value)] = c + 1;
                }
                _out.WriteLine("   render-shell normal buckets: " +
                    string.Join(" ", buckets.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            }
        }
    }

    [Fact]
    public void PortalSide_CentroidVsDatBit_AtGreyEye()
    {
        var datDir = DatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var eyeWorld = new Vector3(135.51f, 83.32f, 69.16f);

        var disagreements = new List<string>();
        bool sawConnectorBackPortal = false;
        bool connectorCentroidCulls = false, connectorDatAdmits = false;

        foreach (uint cellId in new[] { 0xF6820116u, 0xF6820117u, 0xF6820118u })
        {
            var env = dats.Get<EnvCell>(cellId);
            if (env is null) { _out.WriteLine($"cell 0x{cellId:X8}: EnvCell NOT FOUND"); continue; }
            var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | env.EnvironmentId);
            if (environment is null || !environment.Cells.TryGetValue(env.CellStructure, out var cs) || cs is null)
            { _out.WriteLine($"cell 0x{cellId:X8}: no CellStruct"); continue; }

            var origin = new Vector3(env.Position.Origin.X, env.Position.Origin.Y, env.Position.Origin.Z);
            var orient = new Quaternion(env.Position.Orientation.X, env.Position.Orientation.Y,
                                        env.Position.Orientation.Z, env.Position.Orientation.W);
            var world = Matrix4x4.CreateFromQuaternion(orient) * Matrix4x4.CreateTranslation(origin);
            Matrix4x4.Invert(world, out var inverse);
            var localEye = Vector3.Transform(eyeWorld, inverse);

            var bmin = new Vector3(float.MaxValue); var bmax = new Vector3(float.MinValue);
            foreach (var kv in cs.VertexArray!.Vertices)
            { var p = new Vector3(kv.Value.Origin.X, kv.Value.Origin.Y, kv.Value.Origin.Z);
              bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); }
            var centroid = (bmin + bmax) * 0.5f;

            _out.WriteLine($"=== cell 0x{cellId:X8}  localEye=({localEye.X:F2},{localEye.Y:F2},{localEye.Z:F2}) ===");
            foreach (var portal in env.CellPortals!)
            {
                if (!cs.Polygons!.TryGetValue(portal.PolygonId, out var poly) || poly.VertexIds is null || poly.VertexIds.Count < 3)
                { _out.WriteLine($"   p->0x{portal.OtherCellId:X4}: no poly"); continue; }
                Vector3 V(int k) { var v = cs.VertexArray.Vertices[(ushort)poly.VertexIds[k]]; return new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z); }
                var p0 = V(0); var p1 = V(1); var p2 = V(2);
                var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                float d = -Vector3.Dot(normal, p0);

                float centroidDot = Vector3.Dot(normal, centroid) + d;
                int centroidInside = centroidDot >= 0 ? 0 : 1;

                ushort flags = (ushort)portal.Flags;
                bool portalSide = (flags & 0x2) == 0;
                int datInside = portalSide ? 1 : 0;

                float eyeDot = Vector3.Dot(normal, localEye) + d;
                const float eps = 0.01f;
                bool admitCentroid = centroidInside == 0 ? eyeDot >= -eps : eyeDot <= eps;
                bool admitDat      = datInside     == 0 ? eyeDot >= -eps : eyeDot <= eps;

                if (centroidInside != datInside)
                    disagreements.Add($"0x{cellId & 0xFFFF:X4}->0x{portal.OtherCellId:X4}");
                if (cellId == 0xF6820118u && portal.OtherCellId == 0x0116)
                {
                    sawConnectorBackPortal = true;
                    connectorCentroidCulls = !admitCentroid;
                    connectorDatAdmits = admitDat;
                }

                string flag = centroidInside != datInside ? "  <<< DISAGREE" : "";
                string verdict = admitCentroid != admitDat ? $"  centroid={(admitCentroid ? "ADMIT" : "CULL")} dat={(admitDat ? "ADMIT" : "CULL")}" : "";
                _out.WriteLine($"   p->0x{portal.OtherCellId:X4}  flags=0x{flags:X2} PortalSide={portalSide,-5} " +
                               $"centroidInside={centroidInside} datInside={datInside}  eyeDot={eyeDot,7:F3}  " +
                               $"admit[centroid={admitCentroid,-5} dat={admitDat,-5}]{flag}{verdict}");
            }
        }

        Assert.True(sawConnectorBackPortal, "0xF6820118->0116 portal not found in the dat");
        Assert.True(connectorCentroidCulls, "the OLD centroid rule should CULL 0118->0116 at the grey eye (the bug)");
        Assert.True(connectorDatAdmits, "the dat PortalSide bit should ADMIT 0118->0116 at the grey eye (the fix, matches retail)");
        Assert.Equal(new[] { "0x0118->0x0116" }, disagreements.ToArray());
    }

    private static Vector3? PolyNormal(DatReaderWriter.Types.Polygon poly, DatReaderWriter.Types.VertexArray va)
    {
        if (poly.VertexIds is null || poly.VertexIds.Count < 3) return null;
        if (!va.Vertices.TryGetValue((ushort)poly.VertexIds[0], out var a)) return null;
        if (!va.Vertices.TryGetValue((ushort)poly.VertexIds[1], out var b)) return null;
        if (!va.Vertices.TryGetValue((ushort)poly.VertexIds[2], out var c)) return null;
        var n = Vector3.Cross(
            new Vector3(b.Origin.X, b.Origin.Y, b.Origin.Z) - new Vector3(a.Origin.X, a.Origin.Y, a.Origin.Z),
            new Vector3(c.Origin.X, c.Origin.Y, c.Origin.Z) - new Vector3(a.Origin.X, a.Origin.Y, a.Origin.Z));
        return n.LengthSquared() < 1e-9f ? (Vector3?)null : Vector3.Normalize(n);
    }

    private static string DomAxis(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az) return n.X >= 0 ? "+X" : "-X";
        if (ay >= ax && ay >= az) return n.Y >= 0 ? "+Y" : "-Y";
        return n.Z >= 0 ? "+Z" : "-Z";
    }
}
