using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public sealed class DoorVanishDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public DoorVanishDiagnosticTests(ITestOutputHelper output) => _out = output;

    private const uint DoorSetupId = 0x020019FFu;

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpDoorSetup_DrawingBspCoverage()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var setup = dats.Get<DatReaderWriter.DBObjs.Setup>(DoorSetupId);
        Assert.NotNull(setup);
        _out.WriteLine($"=== Setup 0x{DoorSetupId:X8}: Flags={setup!.Flags} parts={setup.Parts.Count} ===");

        foreach (var partId in setup.Parts.Distinct())
        {
            DumpGfxObjBspCoverage(dats, partId);

            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(partId);
            if (gfx is not null && gfx.Flags.HasFlag(DatReaderWriter.Enums.GfxObjFlags.HasDIDDegrade) && gfx.DIDDegrade != 0)
            {
                var ddi = dats.Get<DatReaderWriter.DBObjs.GfxObjDegradeInfo>(gfx.DIDDegrade);
                if (ddi is null) { _out.WriteLine($"  DIDDegrade=0x{gfx.DIDDegrade:X8} MISSING"); continue; }
                _out.WriteLine($"  DIDDegrade=0x{gfx.DIDDegrade:X8} entries={ddi.Degrades.Count} ids=[{string.Join(",", ddi.Degrades.Select(d => $"0x{d.Id:X8}"))}]");
                foreach (var d in ddi.Degrades.Select(d => (uint)d.Id).Distinct())
                {
                    if (d == 0 || d == partId) { _out.WriteLine($"  (degrade 0x{d:X8} == base or zero — skipped)"); continue; }
                    DumpGfxObjBspCoverage(dats, d);
                }
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpHoltburgBuildings_OrphanGeometry()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var modelIds = new SortedSet<uint>();
        foreach (uint lb in new uint[] { 0xA9B40000u, 0xA9B30000u, 0xAAB30000u, 0xA9B50000u, 0xAAB40000u })
        {
            var lbi = dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>(lb | 0xFFFEu);
            if (lbi is null) continue;
            foreach (var b in lbi.Buildings ?? new()) modelIds.Add(b.ModelId);
        }
        _out.WriteLine($"building models: [{string.Join(",", modelIds.Select(m => $"0x{m:X8}"))}]");

        foreach (var mid in modelIds)
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(mid);
            if (gfx?.DrawingBSP?.Root is null) { _out.WriteLine($"0x{mid:X8}: no gfx/BSP"); continue; }

            var walked = new HashSet<ushort>();
            void Walk(DatReaderWriter.Types.DrawingBSPNode? n)
            {
                if (n is null) return;
                if (n.Polygons is not null) foreach (var pid in n.Polygons) walked.Add((ushort)pid);
                Walk(n.PosNode); Walk(n.NegNode);
            }
            Walk(gfx.DrawingBSP.Root);

            var portalPolyIds = new HashSet<ushort>();
            var portalElemDumped = false;
            void WalkPortals(DatReaderWriter.Types.DrawingBSPNode? n)
            {
                if (n is null) return;
                if (n.Portals is not null)
                {
                    foreach (var elem in (IEnumerable)n.Portals)
                    {
                        if (elem is null) continue;
                        var et = elem.GetType();
                        if (!portalElemDumped)
                        {
                            portalElemDumped = true;
                            var props = et.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                .Select(p => $"{p.PropertyType.Name} {p.Name}");
                            _out.WriteLine($"  [portal elem type] {et.Name}: {string.Join("; ", props)}");
                        }
                        if (elem is ushort us) portalPolyIds.Add(us);
                        else if (elem is short s && s >= 0) portalPolyIds.Add((ushort)s);
                        else
                            foreach (var p in et.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                var v = p.GetValue(elem);
                                if (v is ushort pu && p.Name.Contains("Poly", StringComparison.OrdinalIgnoreCase)) portalPolyIds.Add(pu);
                                else if (v is short ps && ps >= 0 && p.Name.Contains("Poly", StringComparison.OrdinalIgnoreCase)) portalPolyIds.Add((ushort)ps);
                                else if (v is int pi && pi is >= 0 and <= ushort.MaxValue && p.Name.Contains("Poly", StringComparison.OrdinalIgnoreCase)) portalPolyIds.Add((ushort)pi);
                            }
                    }
                }
                WalkPortals(n.PosNode); WalkPortals(n.NegNode);
            }
            WalkPortals(gfx.DrawingBSP.Root);

            var orphans = gfx.Polygons.Keys.Where(k => !walked.Contains(k)).OrderBy(k => k).ToList();
            var orphansInPortals = orphans.Where(portalPolyIds.Contains).ToList();
            var orphansTrue = orphans.Where(o => !portalPolyIds.Contains(o)).ToList();
            _out.WriteLine("");
            _out.WriteLine($"=== model 0x{mid:X8}: dict={gfx.Polygons.Count} walked={walked.Count} orphans={orphans.Count} | portalPolyIds={portalPolyIds.Count} [{string.Join(",", portalPolyIds.OrderBy(x => x))}] | orphans-in-portals={orphansInPortals.Count} TRUE-orphans=[{string.Join(",", orphansTrue)}] ===");
            foreach (var pid in orphans)
            {
                var poly = gfx.Polygons[pid];
                if (poly.VertexIds.Count < 3) { _out.WriteLine($"  poly {pid}: degenerate"); continue; }
                var pts = new List<Vector3>();
                bool ok = true;
                foreach (var vid in poly.VertexIds)
                {
                    if (!gfx.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { ok = false; break; }
                    pts.Add(new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z));
                }
                if (!ok) continue;
                var n2 = NewellNormal(pts);
                var c = pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Count;
                float sx = pts.Max(p => p.X) - pts.Min(p => p.X);
                float sy = pts.Max(p => p.Y) - pts.Min(p => p.Y);
                float sz = pts.Max(p => p.Z) - pts.Min(p => p.Z);
                bool vertical = MathF.Abs(n2.Z) < 0.15f;
                float horizSpan = MathF.Max(sx, sy);
                bool doorish = vertical && sz is > 1.5f and < 3.5f && horizSpan is > 0.7f and < 2.5f;
                _out.WriteLine(
                    $"  poly {pid,3}: c=({c.X:F2},{c.Y:F2},{c.Z:F2}) n=({n2.X:F2},{n2.Y:F2},{n2.Z:F2}) " +
                    $"span=({sx:F1},{sy:F1},{sz:F1}) verts={pts.Count}{(doorish ? "  <== DOOR-SIZED VERTICAL QUAD" : vertical ? " (vertical)" : MathF.Abs(n2.Z) > 0.9f ? " (flat)" : " (ramp)")}");
            }
        }
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

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_ReplicateProductionEmission_OnPortalFills()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint mid in new uint[] { 0x010014C3u, 0x01000827u })
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(mid)!;
            var walked = new HashSet<ushort>();
            void Walk(DatReaderWriter.Types.DrawingBSPNode? n)
            {
                if (n is null) return;
                if (n.Polygons is not null) foreach (var pid in n.Polygons) walked.Add((ushort)pid);
                Walk(n.PosNode); Walk(n.NegNode);
            }
            Walk(gfx.DrawingBSP!.Root);
            var fills = gfx.Polygons.Keys.Where(k => !walked.Contains(k)).OrderBy(k => k).ToList();

            _out.WriteLine($"=== model 0x{mid:X8} ===");
            foreach (var pid in fills)
            {
                var poly = gfx.Polygons[pid];
                bool noPos = poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.NoPos);
                bool hasNeg = poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.Negative)
                           || poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.Both)
                           || (!poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.NoNeg)
                               && poly.SidesType == DatReaderWriter.Enums.CullMode.Clockwise);
                _out.WriteLine(
                    $"  poly {pid,3}: stip={poly.Stippling}(raw={(int)poly.Stippling}) sides={poly.SidesType}(raw={(int)poly.SidesType}) " +
                    $"posSurf={poly.PosSurface} negSurf={poly.NegSurface} " +
                    $"-> production emits: pos={!noPos} neg={hasNeg && poly.NegSurface >= 0}");
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpPortalFillSurfaceTypes()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint mid in new uint[] { 0x010014C3u, 0x01000827u, 0x0100082Eu, 0x01000C17u })
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(mid);
            if (gfx?.DrawingBSP?.Root is null) continue;

            var walked = new HashSet<ushort>();
            void Walk(DatReaderWriter.Types.DrawingBSPNode? n)
            {
                if (n is null) return;
                if (n.Polygons is not null) foreach (var pid in n.Polygons) walked.Add((ushort)pid);
                Walk(n.PosNode); Walk(n.NegNode);
            }
            Walk(gfx.DrawingBSP.Root);
            var portalFills = gfx.Polygons.Keys.Where(k => !walked.Contains(k)).OrderBy(k => k).ToList();

            _out.WriteLine($"=== model 0x{mid:X8}: {portalFills.Count} portal-fill polys; Surfaces list has {gfx.Surfaces.Count} entries ===");
            foreach (var pid in portalFills)
            {
                var poly = gfx.Polygons[pid];
                string Describe(short surfIdx)
                {
                    if (surfIdx < 0 || surfIdx >= gfx.Surfaces.Count) return $"idx{surfIdx}=OOB";
                    uint sid = gfx.Surfaces[surfIdx];
                    var surf = dats.Get<DatReaderWriter.DBObjs.Surface>(sid);
                    if (surf is null) return $"0x{sid:X8}=MISSING";
                    bool textured = surf.Type.HasFlag(DatReaderWriter.Enums.SurfaceType.Base1Image)
                                 || surf.Type.HasFlag(DatReaderWriter.Enums.SurfaceType.Base1ClipMap);
                    return $"0x{sid:X8} type={surf.Type} -> {(textured ? "TEXTURED (drawn)" : "SOLID (skipNoTexture SKIPS on building/cell pass)")}";
                }
                _out.WriteLine($"  poly {pid,3}: sides={poly.SidesType} stip={poly.Stippling} pos[{Describe(poly.PosSurface)}] neg[{Describe(poly.NegSurface)}]");
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpControls_HallAndCottage()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        DumpGfxObjBspCoverage(dats, 0x010014C3u);   // meeting hall shell
        DumpGfxObjBspCoverage(dats, 0x01000827u);
    }

    private void DumpGfxObjBspCoverage(DatCollection dats, uint gfxId)
    {
        var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(gfxId);
        if (gfx is null) { _out.WriteLine($"GfxObj 0x{gfxId:X8}: MISSING"); return; }

        _out.WriteLine("");
        _out.WriteLine($"=== GfxObj 0x{gfxId:X8}: Flags={gfx.Flags} dictPolys={gfx.Polygons.Count} physPolys={gfx.PhysicsPolygons?.Count ?? 0} verts={gfx.VertexArray.Vertices.Count} ===");

        if (gfx.DrawingBSP?.Root is null)
        {
            _out.WriteLine("  DrawingBSP: NULL (filter would draw everything — door vanish NOT explained here)");
            return;
        }

        // --- 1. The e46d3d9 walk: PosNode/NegNode + node.Polygons only ---
        var walked = new HashSet<ushort>();
        var nodeTypes = new Dictionary<string, int>();
        void Walk(DatReaderWriter.Types.DrawingBSPNode? node)
        {
            if (node is null) return;
            var tn = node.GetType().Name;
            nodeTypes[tn] = nodeTypes.TryGetValue(tn, out var c) ? c + 1 : 1;
            if (node.Polygons is not null)
                foreach (var pid in node.Polygons) walked.Add((ushort)pid);
            Walk(node.PosNode);
            Walk(node.NegNode);
        }
        Walk(gfx.DrawingBSP.Root);

        _out.WriteLine($"  node runtime types: {string.Join(", ", nodeTypes.Select(kv => $"{kv.Key}×{kv.Value}"))}");
        _out.WriteLine($"  e46d3d9 walk referenced={walked.Count} / dict={gfx.Polygons.Count}");

        var orphans = gfx.Polygons.Keys.Where(k => !walked.Contains(k)).OrderBy(k => k).ToList();
        _out.WriteLine($"  ORPHANS (filter would DROP): {orphans.Count} ids=[{string.Join(",", orphans.Take(60))}]");

        var seenTypes = new HashSet<Type>();
        var extraRefs = new Dictionary<string, HashSet<ushort>>();
        void Sweep(object? node)
        {
            if (node is null) return;
            var t = node.GetType();
            if (seenTypes.Add(t))
            {
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => $"{p.PropertyType.Name} {p.Name}");
                _out.WriteLine($"  [type] {t.Name}: {string.Join("; ", props)}");
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v;
                try { v = p.GetValue(node); } catch { continue; }
                if (v is null) continue;

                if (p.Name is "PosNode" or "NegNode") { Sweep(v); continue; }

                // any enumerable of ushort/short/int that isn't the known Polygons list
                if (p.Name != "Polygons" && v is IEnumerable en && v is not string)
                {
                    var ids = new HashSet<ushort>();
                    foreach (var item in en)
                    {
                        if (item is ushort us) ids.Add(us);
                        else if (item is short s && s >= 0) ids.Add((ushort)s);
                        else if (item is int i && i is >= 0 and <= ushort.MaxValue) ids.Add((ushort)i);
                        else { ids = null!; break; }
                    }
                    if (ids is { Count: > 0 })
                    {
                        var key = $"{t.Name}.{p.Name}";
                        if (!extraRefs.TryGetValue(key, out var set)) extraRefs[key] = set = new HashSet<ushort>();
                        set.UnionWith(ids);
                    }
                }
                // nested single node-like objects (e.g. a portal payload object)
                else if (v is DatReaderWriter.Types.DrawingBSPNode child && p.Name is not "PosNode" and not "NegNode")
                {
                    Sweep(child);
                }
            }
        }
        Sweep(gfx.DrawingBSP.Root);

        foreach (var (key, set) in extraRefs.OrderBy(k => k.Key))
        {
            var hit = set.Where(id => gfx.Polygons.ContainsKey(id)).OrderBy(x => x).ToList();
            var orphanRescue = hit.Where(id => orphans.Contains(id)).ToList();
            _out.WriteLine($"  [extra ids] {key}: {set.Count} values, {hit.Count} are valid poly ids, {orphanRescue.Count} would rescue orphans [{string.Join(",", orphanRescue.Take(40))}]");
        }

        // --- 3. Orphan geometry: what would the filter have dropped, visually? ---
        if (orphans.Count > 0)
        {
            var omin = new Vector3(float.MaxValue); var omax = new Vector3(float.MinValue);
            int degenerate = 0;
            foreach (var pid in orphans)
            {
                var poly = gfx.Polygons[pid];
                if (poly.VertexIds.Count < 3) { degenerate++; continue; }
                foreach (var vid in poly.VertexIds)
                    if (gfx.VertexArray.Vertices.TryGetValue((ushort)vid, out var v))
                    {
                        var p = new Vector3(v.Origin.X, v.Origin.Y, v.Origin.Z);
                        omin = Vector3.Min(omin, p); omax = Vector3.Max(omax, p);
                    }
            }
            _out.WriteLine($"  orphan AABB=({omin.X:F2},{omin.Y:F2},{omin.Z:F2})..({omax.X:F2},{omax.Y:F2},{omax.Z:F2}) degenerate={degenerate}");
        }

        // --- 4. Full-model AABB for scale ---
        var mmin = new Vector3(float.MaxValue); var mmax = new Vector3(float.MinValue);
        foreach (var kvp in gfx.VertexArray.Vertices)
        {
            var p = new Vector3(kvp.Value.Origin.X, kvp.Value.Origin.Y, kvp.Value.Origin.Z);
            mmin = Vector3.Min(mmin, p); mmax = Vector3.Max(mmax, p);
        }
        _out.WriteLine($"  model AABB=({mmin.X:F2},{mmin.Y:F2},{mmin.Z:F2})..({mmax.X:F2},{mmax.Y:F2},{mmax.Z:F2})");
    }
}
