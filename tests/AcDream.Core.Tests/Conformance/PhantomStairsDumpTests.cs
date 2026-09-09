using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using DatLandBlockInfo = DatReaderWriter.DBObjs.LandBlockInfo;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public sealed class PhantomStairsDumpTests
{
    private readonly ITestOutputHelper _out;
    public PhantomStairsDumpTests(ITestOutputHelper output) => _out = output;

    private const uint Landblock = 0xA9B30000u;
    private const uint EnvironmentFilePrefix = 0x0D000000u;

    // User-observed phantom-stairs spot (A9B3-local frame; world y + 192).
    private static readonly Vector3 StairsSpot = new(183.0f, 81.0f, 116.5f);
    private static readonly Vector3 GapPoint = new(184.915f, 82.464f, 116.48f);

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpA9B3_Buildings_And_InteriorCells()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var lbi = dats.Get<DatLandBlockInfo>(Landblock | 0xFFFEu);
        Assert.NotNull(lbi);
        _out.WriteLine($"=== LandBlockInfo 0x{Landblock | 0xFFFEu:X8}: NumCells={lbi!.NumCells}, Buildings={lbi.Buildings?.Count ?? 0} ===");

        var cells = new Dictionary<uint, (DatEnvCell Cell, List<Vector3> WorldVerts, Vector3 Min, Vector3 Max, List<float> FlatHeights, int RampPolys)>();
        for (uint low = 0x0100; low < 0x0100 + lbi.NumCells; low++)
        {
            uint id = Landblock | low;
            var dc = dats.Get<DatEnvCell>(id);
            if (dc is null) { _out.WriteLine($"  cell 0x{id:X8}: MISSING"); continue; }

            var env = dats.Get<DatEnvironment>(EnvironmentFilePrefix | dc.EnvironmentId);
            if (env is null || !env.Cells.TryGetValue(dc.CellStructure, out var cs) || cs is null)
            { _out.WriteLine($"  cell 0x{id:X8}: env 0x{dc.EnvironmentId:X} struct {dc.CellStructure} MISSING"); continue; }

            var world = ConformanceDats.WorldTransform(dc);
            var verts = new List<Vector3>(cs.VertexArray.Vertices.Count);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var kvp in cs.VertexArray.Vertices)
            {
                var p = Vector3.Transform(
                    new Vector3(kvp.Value.Origin.X, kvp.Value.Origin.Y, kvp.Value.Origin.Z), world);
                verts.Add(p);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var flatHeights = new List<float>();
            int rampPolys = 0;
            foreach (var poly in cs.Polygons.Values)
            {
                if (poly.VertexIds.Count < 3) continue;
                var pts = new List<Vector3>(poly.VertexIds.Count);
                bool ok = true;
                foreach (var vid in poly.VertexIds)
                {
                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { ok = false; break; }
                    pts.Add(Vector3.Transform(new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z), world));
                }
                if (!ok) continue;
                var n = Vector3.Zero;
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Count];
                    n.X += (a.Y - b.Y) * (a.Z + b.Z);
                    n.Y += (a.Z - b.Z) * (a.X + b.X);
                    n.Z += (a.X - b.X) * (a.Y + b.Y);
                }
                if (n.LengthSquared() < 1e-10f) continue;
                n = Vector3.Normalize(n);
                if (MathF.Abs(n.Z) > 0.9f)
                    flatHeights.Add(pts.Average(p => p.Z));
                else if (MathF.Abs(n.Z) > 0.15f)
                    rampPolys++;
            }

            cells[id] = (dc, verts, min, max, flatHeights, rampPolys);
        }

        _out.WriteLine("");
        _out.WriteLine("=== Interior cells (positions are A9B3-local; world y = local y - 192) ===");
        foreach (var (id, c) in cells.OrderBy(k => k.Key))
        {
            var dc = c.Cell;
            var o = dc.Position.Origin;
            var q = dc.Position.Orientation;
            var distinctHeights = c.FlatHeights.Select(h => MathF.Round(h, 1)).Distinct().OrderBy(h => h).ToList();
            string stairSig = distinctHeights.Count > 3
                ? $"STAIR? heights=[{string.Join(",", distinctHeights.Select(h => h.ToString("F1")))}]"
                : $"heights=[{string.Join(",", distinctHeights.Select(h => h.ToString("F1")))}]";

            _out.WriteLine(
                $"cell 0x{id:X8} env=0x{dc.EnvironmentId:X4} struct={dc.CellStructure} " +
                $"pos=({o.X:F2},{o.Y:F2},{o.Z:F2}) quat=(w{q.W:F3} x{q.X:F3} y{q.Y:F3} z{q.Z:F3}) " +
                $"flags={dc.Flags} portals=[{string.Join(",", dc.CellPortals.Select(p => $"0x{p.OtherCellId:X3}({p.Flags})"))}] " +
                $"vis={dc.VisibleCells?.Count ?? 0} statics={dc.StaticObjects?.Count ?? 0}");
            _out.WriteLine(
                $"           AABB local=({c.Min.X:F1},{c.Min.Y:F1},{c.Min.Z:F1})..({c.Max.X:F1},{c.Max.Y:F1},{c.Max.Z:F1}) " +
                $"rampPolys={c.RampPolys} {stairSig}");

            bool nearStairs = AabbContains(c.Min, c.Max, StairsSpot, 1.5f);
            bool hasGap = AabbContains(c.Min, c.Max, GapPoint, 0.0f);
            if (nearStairs) _out.WriteLine($"           *** AABB within 1.5m of phantom-stairs spot ({StairsSpot.X},{StairsSpot.Y},{StairsSpot.Z}) ***");
            if (hasGap) _out.WriteLine($"           *** AABB contains gap point ({GapPoint.X},{GapPoint.Y},{GapPoint.Z}) ***");
        }

        // ---- buildings ----
        _out.WriteLine("");
        _out.WriteLine("=== Buildings ===");
        int bIdx = 0;
        foreach (var b in lbi.Buildings ?? new())
        {
            var o = b.Frame.Origin;
            var q = b.Frame.Orientation;
            var seeds = (b.Portals ?? new()).Where(p => p.OtherCellId != 0xFFFF)
                .Select(p => (uint)(Landblock | p.OtherCellId)).Distinct().ToList();

            var set = new HashSet<uint>(seeds);
            var queue = new Queue<uint>(seeds);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!cells.TryGetValue(cur, out var cc)) continue;
                foreach (var p in cc.Cell.CellPortals)
                {
                    if (p.OtherCellId == 0xFFFF) continue;
                    uint nid = Landblock | p.OtherCellId;
                    if (set.Add(nid)) queue.Enqueue(nid);
                }
            }

            float dStairs = Vector2.Distance(new Vector2(o.X, o.Y), new Vector2(StairsSpot.X, StairsSpot.Y));
            _out.WriteLine(
                $"building[{bIdx}] model=0x{b.ModelId:X8} pos=({o.X:F2},{o.Y:F2},{o.Z:F2}) " +
                $"quat=(w{q.W:F3} x{q.X:F3} y{q.Y:F3} z{q.Z:F3}) numLeaves={b.NumLeaves} " +
                $"portals={b.Portals?.Count ?? 0} dist2D(stairsSpot)={dStairs:F1}m");
            _out.WriteLine(
                $"           cells=[{string.Join(",", set.OrderBy(x => x).Select(x => $"0x{x & 0xFFFF:X3}"))}]");
            bIdx++;
        }

        _out.WriteLine("");
        _out.WriteLine("=== Containment (AABB-level) at key points ===");
        foreach (var (label, pt) in new[] { ("stairsSpot", StairsSpot), ("gapPoint", GapPoint) })
        {
            var hits = cells.Where(kv => AabbContains(kv.Value.Min, kv.Value.Max, pt, 0f))
                            .Select(kv => $"0x{kv.Key & 0xFFFF:X3}").ToList();
            _out.WriteLine($"{label} ({pt.X:F1},{pt.Y:F1},{pt.Z:F1}): AABB-contained by [{string.Join(",", hits)}]");
        }
    }

    private static bool AabbContains(Vector3 min, Vector3 max, Vector3 p, float pad) =>
        p.X >= min.X - pad && p.X <= max.X + pad &&
        p.Y >= min.Y - pad && p.Y <= max.Y + pad &&
        p.Z >= min.Z - pad && p.Z <= max.Z + pad;

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpA9B3_Statics_OutdoorStabs_And_ShellModel()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var lbi = dats.Get<DatLandBlockInfo>(Landblock | 0xFFFEu);
        Assert.NotNull(lbi);

        _out.WriteLine("=== LandBlockInfo.Objects (outdoor stabs) ===");
        foreach (var stab in lbi!.Objects ?? new())
        {
            var o = stab.Frame.Origin;
            float d = Vector2.Distance(new Vector2(o.X, o.Y), new Vector2(StairsSpot.X, StairsSpot.Y));
            _out.WriteLine($"  stab model=0x{stab.Id:X8} pos=({o.X:F2},{o.Y:F2},{o.Z:F2}) dist2D(stairsSpot)={d:F1}m");
        }

        _out.WriteLine("");
        _out.WriteLine("=== Per-cell StaticObjects (frame is CELL-LOCAL; world = cellPos + local here, identity quat) ===");
        var cellPos = new Vector3(180f, 84f, 116f);
        var staticModels = new HashSet<uint>();
        for (uint low = 0x0100; low < 0x0100 + lbi.NumCells; low++)
        {
            uint id = Landblock | low;
            var dc = dats.Get<DatEnvCell>(id);
            if (dc?.StaticObjects is null || dc.StaticObjects.Count == 0) continue;
            foreach (var s in dc.StaticObjects)
            {
                var o = s.Frame.Origin;
                var w = cellPos + o;
                var q = s.Frame.Orientation;
                float d = Vector2.Distance(new Vector2(w.X, w.Y), new Vector2(StairsSpot.X, StairsSpot.Y));
                staticModels.Add(s.Id);
                _out.WriteLine(
                    $"  cell 0x{low:X3} static model=0x{s.Id:X8} local=({o.X:F2},{o.Y:F2},{o.Z:F2}) " +
                    $"world=({w.X:F2},{w.Y:F2},{w.Z:F2}) quat=(w{q.W:F3} z{q.Z:F3}) dist2D(stairsSpot)={d:F1}m");
            }
        }

        _out.WriteLine("");
        _out.WriteLine("=== Static/shell model AABBs (model-local) ===");
        staticModels.Add(0x01000827u);   // the shell
        foreach (var mid in staticModels.OrderBy(m => m))
            DumpModelAabb(dats, mid);

        _out.WriteLine("");
        _out.WriteLine("=== Shell stair-signature scan near the phantom spot ===");
        DumpStairSignatureNear(dats, 0x01000827u, new Vector3(180f, 84f, 116f),
            boxMin: new Vector3(181.0f, 77.0f, 115.5f), boxMax: new Vector3(186.5f, 83.5f, 121.0f));

        _out.WriteLine("");
        _out.WriteLine("=== Shell WHOLE-MODEL scan (does the shell contain stairs ANYWHERE?) ===");
        DumpStairSignatureNear(dats, 0x01000827u, new Vector3(180f, 84f, 116f),
            boxMin: new Vector3(-10000f), boxMax: new Vector3(10000f));

        // Localize: roofs are big high slopes; stairs are a narrow low strip.
        _out.WriteLine("");
        _out.WriteLine("=== Shell ramp/low-flat poly localization (world local-frame, placed at 180,84,116) ===");
        var shell = dats.Get<DatReaderWriter.DBObjs.GfxObj>(0x01000827u)!;
        foreach (var kv in shell.Polygons)
        {
            var poly = kv.Value;
            if (poly.VertexIds.Count < 3) continue;
            var pts = new List<Vector3>();
            bool ok = true;
            foreach (var vid in poly.VertexIds)
            {
                if (!shell.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { ok = false; break; }
                pts.Add(new Vector3(180f + v.Origin.X, 84f + v.Origin.Y, 116f + v.Origin.Z));
            }
            if (!ok) continue;
            var n = NewellNormal(pts);
            if (n == Vector3.Zero) continue;
            var c = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Count;
            bool ramp = MathF.Abs(n.Z) > 0.15f && MathF.Abs(n.Z) <= 0.9f;
            bool lowFlat = MathF.Abs(n.Z) > 0.9f && c.Z < 120.5f;
            if (!ramp && !lowFlat) continue;
            _out.WriteLine(
                $"  poly {kv.Key,3} {(ramp ? "RAMP" : "FLAT")} c=({c.X:F1},{c.Y:F1},{c.Z:F2}) nZ={n.Z:F2} verts={pts.Count} " +
                $"xy-span=({pts.Max(p => p.X) - pts.Min(p => p.X):F1},{pts.Max(p => p.Y) - pts.Min(p => p.Y):F1})");
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Dump_Cell104_ExteriorPortalPlane_Vs_GapPoint()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint low in new uint[] { 0x0104, 0x0102, 0x0105, 0x0100 })
        {
            uint id = Landblock | low;
            var dc = dats.Get<DatEnvCell>(id);
            if (dc is null) continue;
            var env = dats.Get<DatEnvironment>(EnvironmentFilePrefix | dc.EnvironmentId);
            if (env is null || !env.Cells.TryGetValue(dc.CellStructure, out var cs) || cs is null) continue;
            var world = ConformanceDats.WorldTransform(dc);

            foreach (var p in dc.CellPortals)
            {
                if (p.OtherCellId != 0xFFFF) continue;
                if (!cs.Polygons.TryGetValue(p.PolygonId, out var poly)) { _out.WriteLine($"cell 0x{low:X3}: portal poly {p.PolygonId} MISSING"); continue; }
                var pts = new List<Vector3>();
                foreach (var vid in poly.VertexIds)
                    if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v))
                        pts.Add(Vector3.Transform(new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z), world));
                if (pts.Count < 3) continue;

                var n = Vector3.Zero;
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    n.X += (a.Y - b.Y) * (a.Z + b.Z);
                    n.Y += (a.Z - b.Z) * (a.X + b.X);
                    n.Z += (a.X - b.X) * (a.Y + b.Y);
                }
                n = Vector3.Normalize(n);
                float dPlane = -Vector3.Dot(n, pts[0]);
                float dist = Vector3.Dot(n, GapPoint) + dPlane;
                _out.WriteLine(
                    $"cell 0x{low:X3} EXTERIOR portal poly={p.PolygonId} flags={p.Flags} " +
                    $"verts=[{string.Join(" ", pts.Select(v => $"({v.X:F2},{v.Y:F2},{v.Z:F2})"))}]");
                _out.WriteLine(
                    $"           plane n=({n.X:F3},{n.Y:F3},{n.Z:F3}) d={dPlane:F3} " +
                    $"dist(gapPoint)={dist:F3} | straddle@r=0.48: {(MathF.Abs(dist) < 0.48f + 0.0002f ? "YES — retail ADMITS outside cells here" : "NO — retail keeps curr_cell")}");
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpAAB3_Buildings_And_StairScan()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint lb = 0xAAB30000u;
        // User stood at world (183, -111) = AAB3-local (-9, 81). NPCs at
        // AAB3-local (17.7, 84.4) and (27.7, 83.6).
        var viewer = new Vector2(-9f, 81f);

        var lbi = dats.Get<DatLandBlockInfo>(lb | 0xFFFEu);
        Assert.NotNull(lbi);
        _out.WriteLine($"=== AAB3 LandBlockInfo: NumCells={lbi!.NumCells}, Buildings={lbi.Buildings?.Count ?? 0} ===");

        int bIdx = 0;
        foreach (var b in lbi.Buildings ?? new())
        {
            var o = b.Frame.Origin;
            var q = b.Frame.Orientation;
            float d = Vector2.Distance(new Vector2(o.X, o.Y), viewer);
            var seeds = (b.Portals ?? new()).Where(p => p.OtherCellId != 0xFFFF)
                .Select(p => p.OtherCellId).Distinct().OrderBy(x => x).ToList();
            _out.WriteLine(
                $"building[{bIdx}] model=0x{b.ModelId:X8} pos=({o.X:F2},{o.Y:F2},{o.Z:F2}) " +
                $"quat=(w{q.W:F3} z{q.Z:F3}) portals={b.Portals?.Count ?? 0} " +
                $"seedCells=[{string.Join(",", seeds.Select(s => $"0x{s:X3}"))}] dist(viewer)={d:F1}m");
            bIdx++;
        }

        _out.WriteLine("");
        _out.WriteLine("=== Per-building-model whole-shell stair scan ===");
        foreach (var mid in (lbi.Buildings ?? new()).Select(b => b.ModelId).Distinct().OrderBy(m => m))
            ScanModelStairs(dats, mid);

        _out.WriteLine("");
        _out.WriteLine("=== AAB3 LandBlockInfo.Objects (outdoor stabs) — sorted by dist to viewer ===");
        var stabs = (lbi.Objects ?? new())
            .Select(s => (s, d: Vector2.Distance(new Vector2(s.Frame.Origin.X, s.Frame.Origin.Y), viewer)))
            .OrderBy(t => t.d).ToList();
        foreach (var (s, d) in stabs)
        {
            var o = s.Frame.Origin; var q = s.Frame.Orientation;
            _out.WriteLine($"  stab model=0x{s.Id:X8} pos=({o.X:F2},{o.Y:F2},{o.Z:F2}) quat=(w{q.W:F3} z{q.Z:F3}) dist={d:F1}m  [world=({o.X + 192f:F1},{o.Y - 192f:F1},{o.Z:F1})]");
        }

        _out.WriteLine("");
        _out.WriteLine("=== Stair scan per stab model within 45m of viewer ===");
        foreach (var mid in stabs.Where(t => t.d < 45f).Select(t => t.s.Id).Distinct().OrderBy(m => m))
            ScanModelStairs(dats, mid);

        _out.WriteLine("");
        _out.WriteLine("=== AAB3 interior cells (ALL — no distance filter) ===");
        for (uint low = 0x0100; low < 0x0100 + lbi.NumCells; low++)
        {
            var dc = dats.Get<DatEnvCell>(lb | low);
            if (dc is null) continue;
            var env = dats.Get<DatEnvironment>(EnvironmentFilePrefix | dc.EnvironmentId);
            if (env is null || !env.Cells.TryGetValue(dc.CellStructure, out var cs) || cs is null) continue;
            var world = ConformanceDats.WorldTransform(dc);
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            var flat = new List<float>(); int ramps = 0;
            foreach (var poly in cs.Polygons.Values)
            {
                if (poly.VertexIds.Count < 3) continue;
                var pts = new List<Vector3>();
                bool ok = true;
                foreach (var vid in poly.VertexIds)
                {
                    if (!cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { ok = false; break; }
                    var p = Vector3.Transform(new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z), world);
                    pts.Add(p); min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
                if (!ok) continue;
                var n = NewellNormal(pts);
                if (n.LengthSquared() < 1e-10f) continue;
                if (MathF.Abs(n.Z) > 0.9f) flat.Add(pts.Average(p => p.Z));
                else if (MathF.Abs(n.Z) > 0.15f) ramps++;
            }
            var c2 = new Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2);
            float dist = Vector2.Distance(c2, viewer);
            var heights = flat.Select(h => MathF.Round(h, 1)).Distinct().OrderBy(h => h).ToList();
            string sig = (heights.Count > 3 || ramps > 0) ? $" <== STAIR-ISH ramps={ramps} heights=[{string.Join(",", heights.Select(h => h.ToString("F1")))}]" : "";
            _out.WriteLine(
                $"cell 0x{low:X3} env=0x{dc.EnvironmentId:X4} flags={dc.Flags} statics={dc.StaticObjects?.Count ?? 0} " +
                $"AABB=({min.X:F1},{min.Y:F1},{min.Z:F1})..({max.X:F1},{max.Y:F1},{max.Z:F1}) dist={dist:F1}m{sig}");
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpAAB3_Watchtower_DoorAlignment_And_RampFaces()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint lb = 0xAAB30000u;
        var lbi = dats.Get<DatLandBlockInfo>(lb | 0xFFFEu);
        Assert.NotNull(lbi);
        var b = lbi!.Buildings![0];
        var shellWorld =
            Matrix4x4.CreateFromQuaternion(b.Frame.Orientation) *
            Matrix4x4.CreateTranslation(b.Frame.Origin);
        _out.WriteLine($"tower model=0x{b.ModelId:X8} frame=({b.Frame.Origin.X},{b.Frame.Origin.Y},{b.Frame.Origin.Z}) quat=(w{b.Frame.Orientation.W:F3} z{b.Frame.Orientation.Z:F3})");
        _out.WriteLine($"bld portals: [{string.Join("; ", (b.Portals ?? new()).Select(p => $"other=0x{p.OtherCellId:X3} otherPortal={p.OtherPortalId} flags={p.Flags} stabs={p.StabList?.Count ?? 0}"))}]");

        // -- tower model ramp localization (model-local, z bins) --
        var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(b.ModelId)!;
        var bins = new Dictionary<string, (int N, Vector3 Sum, float MinZ, float MaxZ)>();
        foreach (var poly in gfx.Polygons.Values)
        {
            var pts = ResolveLocalPoly(gfx, poly);
            if (pts is null) continue;
            var n = NewellNormal(pts);
            if (n == Vector3.Zero || MathF.Abs(n.Z) <= 0.15f || MathF.Abs(n.Z) > 0.9f) continue;
            var c = pts.Aggregate(Vector3.Zero, (a, p) => a + p) / pts.Count;
            string bin = c.Z < 4f ? "z<4 (ground stairs?)" : c.Z < 12f ? "z 4-12 (mid stairs?)" : "z>12 (roof)";
            var e = bins.TryGetValue(bin, out var v) ? v : (N: 0, Sum: Vector3.Zero, MinZ: float.MaxValue, MaxZ: float.MinValue);
            bins[bin] = (e.N + 1, e.Sum + c, MathF.Min(e.MinZ, c.Z), MathF.Max(e.MaxZ, c.Z));
        }
        _out.WriteLine("=== tower ramp polys by z-bin (model-local centroid mean) ===");
        foreach (var (bin, v) in bins.OrderBy(k => k.Key))
        {
            var mean = v.Sum / v.N;
            var w = Vector3.Transform(mean, shellWorld);
            _out.WriteLine($"  {bin}: n={v.N} meanLocal=({mean.X:F1},{mean.Y:F1},{mean.Z:F1}) z {v.MinZ:F1}..{v.MaxZ:F1} -> meanWorld=({w.X:F1},{w.Y:F1},{w.Z:F1}) [world abs=({w.X + 192f:F1},{w.Y - 192f:F1})]");
        }

        // -- low flat treads (model z < 9, |nZ|>0.9, small) = stair treads --
        _out.WriteLine("=== tower small flat plates below z=9 (stair treads / landings) ===");
        foreach (var kv in gfx.Polygons)
        {
            var pts = ResolveLocalPoly(gfx, kv.Value);
            if (pts is null) continue;
            var n = NewellNormal(pts);
            if (n == Vector3.Zero || MathF.Abs(n.Z) <= 0.9f) continue;
            var c = pts.Aggregate(Vector3.Zero, (a, p) => a + p) / pts.Count;
            if (c.Z >= 9f || c.Z <= 0.05f) continue;
            float sx = pts.Max(p => p.X) - pts.Min(p => p.X);
            float sy = pts.Max(p => p.Y) - pts.Min(p => p.Y);
            var w = Vector3.Transform(c, shellWorld);
            _out.WriteLine($"  poly {kv.Key,3} c-local=({c.X:F1},{c.Y:F1},{c.Z:F2}) span=({sx:F1},{sy:F1}) -> world=({w.X:F1},{w.Y:F1},{w.Z:F2})");
        }

        _out.WriteLine("=== tower cells (positions are AAB3-local) ===");
        var shellVertPolys = new List<List<Vector3>>();   // world-space vertical shell polys
        foreach (var poly in gfx.Polygons.Values)
        {
            var pts = ResolveLocalPoly(gfx, poly);
            if (pts is null) continue;
            var n = NewellNormal(pts);
            if (n == Vector3.Zero || MathF.Abs(n.Z) > 0.15f) continue;
            shellVertPolys.Add(pts.Select(p => Vector3.Transform(p, shellWorld)).ToList());
        }

        for (uint low = 0x0100; low < 0x0100 + lbi.NumCells; low++)
        {
            var dc = dats.Get<DatEnvCell>(lb | low);
            if (dc is null) continue;
            var env = dats.Get<DatEnvironment>(EnvironmentFilePrefix | dc.EnvironmentId);
            if (env is null || !env.Cells.TryGetValue(dc.CellStructure, out var cs) || cs is null) continue;
            var world = ConformanceDats.WorldTransform(dc);
            var o = dc.Position.Origin; var q = dc.Position.Orientation;
            _out.WriteLine($"cell 0x{low:X3} env=0x{dc.EnvironmentId:X4} pos=({o.X:F2},{o.Y:F2},{o.Z:F2}) quat=(w{q.W:F3} z{q.Z:F3}) flags={dc.Flags} portals=[{string.Join(",", dc.CellPortals.Select(p => $"0x{p.OtherCellId:X3}"))}]");

            foreach (var p in dc.CellPortals)
            {
                if (p.OtherCellId != 0xFFFF) continue;
                if (!cs.Polygons.TryGetValue(p.PolygonId, out var poly)) continue;
                var rect = new List<Vector3>();
                foreach (var vid in poly.VertexIds)
                    if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v))
                        rect.Add(Vector3.Transform(new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z), world));
                if (rect.Count < 3) continue;
                var c = rect.Aggregate(Vector3.Zero, (a, v2) => a + v2) / rect.Count;

                int covered = 0, samples = 0;
                var pn = NewellNormal(rect);
                foreach (var t in new[] { 0.5f, 0.25f, 0.75f })
                {
                    var sample = Vector3.Lerp(rect[0], rect[2 % rect.Count], t);
                    samples++;
                    foreach (var wallPoly in shellVertPolys)
                    {
                        var wn = NewellNormal(wallPoly);
                        if (MathF.Abs(Vector3.Dot(wn, pn)) < 0.9f) continue;          // not parallel
                        float dist = Vector3.Dot(wn, sample - wallPoly[0]);
                        if (MathF.Abs(dist) > 0.3f) continue;
                        if (PointInPolygonOnPlane(sample, wallPoly, wn)) { covered++; break; }
                    }
                }
                _out.WriteLine(
                    $"    EXTERIOR portal rect c=({c.X:F2},{c.Y:F2},{c.Z:F2}) " +
                    $"verts=[{string.Join(" ", rect.Select(v2 => $"({v2.X:F1},{v2.Y:F1},{v2.Z:F1})"))}] " +
                    $"shell-coverage {covered}/{samples} {(covered > 0 ? "<== BLOCKED BY SHELL WALL (misalignment!)" : "(open — aligned)")}");
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpAAB3_Watchtower_TopDownMap()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint lb = 0xAAB30000u;
        var lbi = dats.Get<DatLandBlockInfo>(lb | 0xFFFEu)!;
        var b = lbi.Buildings![0];
        var shellWorld =
            Matrix4x4.CreateFromQuaternion(b.Frame.Orientation) *
            Matrix4x4.CreateTranslation(b.Frame.Origin);
        var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(b.ModelId)!;

        // model AABB world-placed
        var mmin = new Vector3(float.MaxValue); var mmax = new Vector3(float.MinValue);
        foreach (var kvp in gfx.VertexArray.Vertices)
        {
            var w = Vector3.Transform(new Vector3(kvp.Value.Origin.X, kvp.Value.Origin.Y, kvp.Value.Origin.Z), shellWorld);
            mmin = Vector3.Min(mmin, w); mmax = Vector3.Max(mmax, w);
        }
        _out.WriteLine($"tower WORLD AABB (AAB3-local frame): ({mmin.X:F1},{mmin.Y:F1},{mmin.Z:F1})..({mmax.X:F1},{mmax.Y:F1},{mmax.Z:F1})  [world abs x {mmin.X + 192f:F1}..{mmax.X + 192f:F1}, y {mmin.Y - 192f:F1}..{mmax.Y - 192f:F1}]");

        foreach (var (zLo, zHi, label) in new[] { (115.5f, 119.5f, "GROUND band z 115.5-119.5"), (119.5f, 125.5f, "UPPER band z 119.5-125.5") })
        {
            const float x0 = 8f, x1 = 52f, y0 = 66f, y1 = 102f, cs = 0.5f;
            int W = (int)((x1 - x0) / cs), H = (int)((y1 - y0) / cs);
            var grid = new char[H, W];
            for (int r = 0; r < H; r++) for (int c = 0; c < W; c++) grid[r, c] = ' ';

            void Plot(Vector3 p, char ch, bool force = false)
            {
                int c = (int)((p.X - x0) / cs), r = (int)((p.Y - y0) / cs);
                if (c < 0 || c >= W || r < 0 || r >= H) return;
                if (force || grid[r, c] == ' ' || (ch == '/' && grid[r, c] == '#')) grid[r, c] = ch;
            }

            foreach (var poly in gfx.Polygons.Values)
            {
                var ptsLocal = ResolveLocalPoly(gfx, poly);
                if (ptsLocal is null) continue;
                var pts = ptsLocal.Select(p => Vector3.Transform(p, shellWorld)).ToList();
                var n = NewellNormal(pts);
                if (n == Vector3.Zero) continue;
                char ch = MathF.Abs(n.Z) > 0.9f ? '_' : MathF.Abs(n.Z) > 0.15f ? '/' : '#';
                // rasterize edges by sampling
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var bb = pts[(i + 1) % pts.Count];
                    for (float t = 0; t <= 1f; t += 0.05f)
                    {
                        var p = Vector3.Lerp(a, bb, t);
                        if (p.Z < zLo || p.Z > zHi) continue;
                        Plot(p, ch);
                    }
                }
            }

            // overlays (AAB3-local = world x-192, y+192)
            foreach (var s in lbi.Objects ?? new()) Plot(s.Frame.Origin, '*', force: true);
            for (float t = 0; t <= 1f; t += 0.1f)   // Sentry path world (219.74,-108.40,117.2)->(217.10,-108.41,117.9)
                Plot(new Vector3(219.74f - 192f + t * (217.10f - 219.74f), -108.40f + 192f, 117.5f), 'S', force: true);
            Plot(new Vector3(209.683f - 192f, -107.560f + 192f, 116f), 'A', force: true);
            Plot(new Vector3(216.436f - 192f, -108.644f + 192f, 116f), 'U', force: true);
            Plot(new Vector3(b.Frame.Origin.X, b.Frame.Origin.Y, (zLo + zHi) / 2), 'O', force: true);

            _out.WriteLine("");
            _out.WriteLine($"=== {label} ===   (x {x0}..{x1} local = world {x0 + 192}..{x1 + 192}; y rows DESC from {y1 - 192f} to {y0 - 192f} world)");
            for (int r = H - 1; r >= 0; r--)
            {
                var row = new char[W];
                for (int c = 0; c < W; c++) row[c] = grid[r, c];
                float wy = (y0 + r * cs) - 192f;
                _out.WriteLine($"y{wy,7:F1} |{new string(row)}|");
            }
        }
    }

    private static List<Vector3>? ResolveLocalPoly(DatReaderWriter.DBObjs.GfxObj gfx, DatReaderWriter.Types.Polygon poly)
    {
        if (poly.VertexIds.Count < 3) return null;
        var pts = new List<Vector3>(poly.VertexIds.Count);
        foreach (var vid in poly.VertexIds)
        {
            if (!gfx.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) return null;
            pts.Add(new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z));
        }
        return pts;
    }

    private static bool PointInPolygonOnPlane(Vector3 p, List<Vector3> poly, Vector3 n)
    {
        // Project onto the dominant plane axes and run 2D even-odd test.
        Func<Vector3, Vector2> proj;
        var an = Vector3.Abs(n);
        if (an.X >= an.Y && an.X >= an.Z) proj = v => new Vector2(v.Y, v.Z);
        else if (an.Y >= an.Z) proj = v => new Vector2(v.X, v.Z);
        else proj = v => new Vector2(v.X, v.Y);

        var pt = proj(p);
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = proj(poly[i]); var bb = proj(poly[j]);
            if (a.Y > pt.Y != bb.Y > pt.Y &&
                pt.X < (bb.X - a.X) * (pt.Y - a.Y) / (bb.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpHallModel_PolyFlagHistogram()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint mid in new uint[] { 0x010014C3u, 0x01000827u })
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(mid)!;
            _out.WriteLine($"=== model 0x{mid:X8}: Flags={gfx.Flags} polys={gfx.Polygons.Count} physPolys={gfx.PhysicsPolygons?.Count ?? 0} ===");

            var hist = new Dictionary<string, (int N, int Ramps)>();
            foreach (var kv in gfx.Polygons)
            {
                var poly = kv.Value;
                var pts = ResolveLocalPoly(gfx, poly);
                if (pts is null) continue;
                var n = NewellNormal(pts);
                bool ramp = n != Vector3.Zero && MathF.Abs(n.Z) > 0.15f && MathF.Abs(n.Z) <= 0.9f;
                string key = $"stip={poly.Stippling} sides={poly.SidesType} posSurf={poly.PosSurface} negSurf={poly.NegSurface}";
                var e = hist.TryGetValue(key, out var v) ? v : (N: 0, Ramps: 0);
                hist[key] = (e.N + 1, e.Ramps + (ramp ? 1 : 0));
            }
            foreach (var (key, v) in hist.OrderByDescending(k => k.Value.N))
                _out.WriteLine($"  [{v.N,3} polys / {v.Ramps,3} ramps] {key}");

            if (gfx.PhysicsPolygons is not null && gfx.PhysicsPolygons.Count > 0)
            {
                int shared = gfx.Polygons.Keys.Count(k => gfx.PhysicsPolygons.ContainsKey(k));
                _out.WriteLine($"  physPoly ids shared with render polys: {shared}/{gfx.Polygons.Count}");
            }

            if (gfx.DrawingBSP?.Root is not null)
            {
                var referenced = new HashSet<ushort>();
                void Walk(DatReaderWriter.Types.DrawingBSPNode? node)
                {
                    if (node is null) return;
                    if (node.Polygons is not null)
                        foreach (var pid in node.Polygons) referenced.Add((ushort)pid);
                    Walk(node.PosNode);
                    Walk(node.NegNode);
                }
                Walk(gfx.DrawingBSP.Root);

                var orphans = gfx.Polygons.Keys.Where(k => !referenced.Contains(k)).OrderBy(k => k).ToList();
                _out.WriteLine($"  drawingBSP referenced={referenced.Count} dictPolys={gfx.Polygons.Count} ORPHANS={orphans.Count}");
                int orphanRamps = 0; var omin = new Vector3(float.MaxValue); var omax = new Vector3(float.MinValue);
                foreach (var pid in orphans)
                {
                    var pts = ResolveLocalPoly(gfx, gfx.Polygons[pid]);
                    if (pts is null) continue;
                    foreach (var p in pts) { omin = Vector3.Min(omin, p); omax = Vector3.Max(omax, p); }
                    var n = NewellNormal(pts);
                    if (n != Vector3.Zero && MathF.Abs(n.Z) > 0.15f && MathF.Abs(n.Z) <= 0.9f) orphanRamps++;
                }
                if (orphans.Count > 0)
                    _out.WriteLine($"  orphan stats: ramps={orphanRamps} AABB=({omin.X:F1},{omin.Y:F1},{omin.Z:F1})..({omax.X:F1},{omax.Y:F1},{omax.Z:F1}) ids=[{string.Join(",", orphans.Take(40))}]");
            }
            else
            {
                _out.WriteLine("  drawingBSP: NULL");
            }

            if (gfx.Flags.HasFlag(DatReaderWriter.Enums.GfxObjFlags.HasDIDDegrade) && gfx.DIDDegrade != 0)
            {
                var ddi = dats.Get<DatReaderWriter.DBObjs.GfxObjDegradeInfo>(gfx.DIDDegrade);
                if (ddi is null) { _out.WriteLine($"  DIDDegrade=0x{gfx.DIDDegrade:X8} MISSING"); continue; }
                _out.WriteLine($"  DIDDegrade=0x{gfx.DIDDegrade:X8} entries={ddi.Degrades.Count}");
                foreach (var d in ddi.Degrades)
                {
                    _out.WriteLine($"    degrade id=0x{d.Id:X8}");
                    ScanGfxStairs(dats, (uint)d.Id, $"      -> ramp scan 0x{d.Id:X8}");
                }
            }
        }
    }

    private void ScanModelStairs(DatCollection dats, uint modelId)
    {
        if ((modelId & 0xFF000000u) == 0x01000000u)
        {
            ScanGfxStairs(dats, modelId, $"model 0x{modelId:X8}");
        }
        else if ((modelId & 0xFF000000u) == 0x02000000u)
        {
            var setup = dats.Get<DatReaderWriter.DBObjs.Setup>(modelId);
            if (setup is null) { _out.WriteLine($"model 0x{modelId:X8}: Setup MISSING"); return; }
            foreach (var partId in setup.Parts.Distinct())
                ScanGfxStairs(dats, partId, $"model 0x{modelId:X8} part 0x{partId:X8}");
        }
    }

    private void ScanGfxStairs(DatCollection dats, uint gfxId, string label)
    {
        var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(gfxId);
        if (gfx is null) { _out.WriteLine($"{label}: GfxObj MISSING"); return; }
        var flat = new List<float>(); int ramps = 0; int polys = 0;
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var poly in gfx.Polygons.Values)
        {
            if (poly.VertexIds.Count < 3) continue;
            var pts = new List<Vector3>();
            bool ok = true;
            foreach (var vid in poly.VertexIds)
            {
                if (!gfx.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { ok = false; break; }
                var p = new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z);
                pts.Add(p); min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            if (!ok) continue;
            polys++;
            var n = NewellNormal(pts);
            if (n.LengthSquared() < 1e-10f) continue;
            if (MathF.Abs(n.Z) > 0.9f) flat.Add(pts.Average(p => p.Z));
            else if (MathF.Abs(n.Z) > 0.15f) ramps++;
        }
        var heights = flat.Select(h => MathF.Round(h, 1)).Distinct().OrderBy(h => h).ToList();
        string verdict = (heights.Count > 4 || ramps > 2) ? "  <== STAIR-ISH" : "";
        _out.WriteLine(
            $"{label}: polys={polys} ramps={ramps} AABB z {min.Z:F1}..{max.Z:F1} " +
            $"flatHeights=[{string.Join(",", heights.Select(h => h.ToString("F1")))}]{verdict}");
    }

    private static Vector3 NewellNormal(List<Vector3> pts)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n.LengthSquared() < 1e-10f ? Vector3.Zero : Vector3.Normalize(n);
    }

    private void DumpModelAabb(DatCollection dats, uint modelId)
    {
        if ((modelId & 0xFF000000u) == 0x01000000u)
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(modelId);
            if (gfx is null) { _out.WriteLine($"  model 0x{modelId:X8}: GfxObj MISSING"); return; }
            var (min, max, nVerts) = GfxAabb(gfx);
            _out.WriteLine($"  model 0x{modelId:X8} (GfxObj): verts={nVerts} AABB=({min.X:F1},{min.Y:F1},{min.Z:F1})..({max.X:F1},{max.Y:F1},{max.Z:F1}) span=({max.X - min.X:F1},{max.Y - min.Y:F1},{max.Z - min.Z:F1})");
        }
        else if ((modelId & 0xFF000000u) == 0x02000000u)
        {
            var setup = dats.Get<DatReaderWriter.DBObjs.Setup>(modelId);
            if (setup is null) { _out.WriteLine($"  model 0x{modelId:X8}: Setup MISSING"); return; }
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            int total = 0;
            foreach (var partId in setup.Parts)
            {
                var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(partId);
                if (gfx is null) continue;
                var (pmin, pmax, n) = GfxAabb(gfx);
                min = Vector3.Min(min, pmin); max = Vector3.Max(max, pmax); total += n;
            }
            _out.WriteLine($"  model 0x{modelId:X8} (Setup, {setup.Parts.Count} parts, part frames NOT applied): verts={total} AABB≈({min.X:F1},{min.Y:F1},{min.Z:F1})..({max.X:F1},{max.Y:F1},{max.Z:F1})");
        }
        else _out.WriteLine($"  model 0x{modelId:X8}: unknown namespace");
    }

    private static (Vector3 Min, Vector3 Max, int Count) GfxAabb(DatReaderWriter.DBObjs.GfxObj gfx)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        int n = 0;
        foreach (var kvp in gfx.VertexArray.Vertices)
        {
            var p = new Vector3(kvp.Value.Origin.X, kvp.Value.Origin.Y, kvp.Value.Origin.Z);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p); n++;
        }
        return (min, max, n);
    }

    private void DumpStairSignatureNear(DatCollection dats, uint gfxId, Vector3 placeAt, Vector3 boxMin, Vector3 boxMax)
    {
        var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(gfxId);
        if (gfx is null) { _out.WriteLine($"GfxObj 0x{gfxId:X8} MISSING"); return; }

        var flatHeights = new List<float>();
        int polysInBox = 0, ramps = 0;
        foreach (var poly in gfx.Polygons.Values)
        {
            if (poly.VertexIds.Count < 3) continue;
            var pts = new List<Vector3>();
            bool ok = true;
            foreach (var vid in poly.VertexIds)
            {
                if (!gfx.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { ok = false; break; }
                pts.Add(placeAt + new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z));
            }
            if (!ok) continue;
            var c = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Count;
            if (!AabbContains(boxMin, boxMax, c, 0f)) continue;
            polysInBox++;

            var n = Vector3.Zero;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                n.X += (a.Y - b.Y) * (a.Z + b.Z);
                n.Y += (a.Z - b.Z) * (a.X + b.X);
                n.Z += (a.X - b.X) * (a.Y + b.Y);
            }
            if (n.LengthSquared() < 1e-10f) continue;
            n = Vector3.Normalize(n);
            if (MathF.Abs(n.Z) > 0.9f) flatHeights.Add(c.Z);
            else if (MathF.Abs(n.Z) > 0.15f) ramps++;
        }
        var distinct = flatHeights.Select(h => MathF.Round(h, 2)).Distinct().OrderBy(h => h).ToList();
        _out.WriteLine(
            $"shell 0x{gfxId:X8} polys-in-box={polysInBox} ramps={ramps} " +
            $"flat heights=[{string.Join(",", distinct.Select(h => h.ToString("F2")))}]");
        _out.WriteLine(distinct.Count > 3
            ? ">>> SHELL ITSELF HAS STAIR-LIKE GEOMETRY AT THE SPOT (multiple flat heights)"
            : ">>> shell shows no stair signature at the spot");
    }
}
