using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Rendering;

[Trait("Lane", "InstalledDat")]
public class DungeonSeamInspectionTests
{
    private readonly ITestOutputHelper _out;
    public DungeonSeamInspectionTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveDatDir()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        return Directory.Exists(datDir) ? datDir : null;
    }

    private static Matrix4x4 WorldTransform(EnvCell cell)
    {
        var rot = new Quaternion(
            cell.Position.Orientation.X, cell.Position.Orientation.Y,
            cell.Position.Orientation.Z, cell.Position.Orientation.W);
        return Matrix4x4.CreateFromQuaternion(rot)
             * Matrix4x4.CreateTranslation(
                   cell.Position.Origin.X, cell.Position.Origin.Y, cell.Position.Origin.Z);
    }

    private static (EnvCell cell, DatReaderWriter.Types.CellStruct cs)? LoadCell(DatCollection dats, uint cellId)
    {
        var envCell = dats.Get<EnvCell>(cellId);
        if (envCell is null) return null;
        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(0x0D000000u | envCell.EnvironmentId);
        if (environment is null) return null;
        if (!environment.Cells.TryGetValue(envCell.CellStructure, out var cs)) return null;
        return (envCell, cs!);
    }

    private static List<Vector3> WorldVerts(
        DatReaderWriter.Types.CellStruct cs, DatReaderWriter.Types.Polygon poly, Matrix4x4 world)
    {
        var result = new List<Vector3>(poly.VertexIds.Count);
        foreach (var vid in poly.VertexIds)
            if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v))
                result.Add(Vector3.Transform(v.Origin, world));
        return result;
    }

    private static bool WouldDraw(DatReaderWriter.Types.Polygon poly, EnvCell cell) =>
        poly.VertexIds.Count >= 3
        && !poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.NoPos)
        && poly.PosSurface >= 0
        && poly.PosSurface < cell.Surfaces.Count;

    [Theory]
    [InlineData(0x8A02016Eu)]
    [InlineData(0x8A02011Eu)]   // the under-hall at z=−12 those portals lead to
    [InlineData(0x8A02017Au)]
    [InlineData(0x8A020182u)]
    [InlineData(0x8A020183u)]
    public void PortalPolys_SurfaceAndDrawVerdict_Dump(uint cellId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var loaded = LoadCell(dats, cellId);
        Assert.NotNull(loaded);
        var (cell, cs) = loaded!.Value;
        var world = WorldTransform(cell);

        _out.WriteLine($"=== 0x{cellId:X8} Env=0x{cell.EnvironmentId:X4} struct={cell.CellStructure} " +
                       $"pos=({cell.Position.Origin.X:F2},{cell.Position.Origin.Y:F2},{cell.Position.Origin.Z:F2}) ===");
        _out.WriteLine($"  Surfaces ({cell.Surfaces.Count}): " +
                       string.Join(" ", cell.Surfaces.ConvertAll(s => $"0x{0x08000000u | (uint)s:X8}")));
        _out.WriteLine($"  visualPolys={cs.Polygons.Count} physicsPolys={cs.PhysicsPolygons.Count} portals={cell.CellPortals.Count}");

        _out.WriteLine($"  StaticObjects={cell.StaticObjects.Count}");
        foreach (var so in cell.StaticObjects)
            _out.WriteLine($"    static id=0x{so.Id:X8} at ({so.Frame.Origin.X:F2},{so.Frame.Origin.Y:F2},{so.Frame.Origin.Z:F2})");

        foreach (var p in cell.CellPortals)
        {
            if (!cs.Polygons.TryGetValue((ushort)p.PolygonId, out var poly))
            {
                _out.WriteLine($"  portal poly={p.PolygonId} -> 0x{p.OtherCellId:X4} [{p.Flags}] NOT IN VISUAL TABLE");
                continue;
            }

            var w = WorldVerts(cs, poly, world);
            var n = w.Count >= 3
                ? Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0]))
                : Vector3.Zero;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var v in w) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }

            bool drawn = WouldDraw(poly, cell);
            string surfInfo = "posSurf=OUT-OF-RANGE";
            if (poly.PosSurface >= 0 && poly.PosSurface < cell.Surfaces.Count)
            {
                uint surfaceId = 0x08000000u | (uint)cell.Surfaces[poly.PosSurface];
                var surface = dats.Get<Surface>(surfaceId);
                surfInfo = surface is null
                    ? $"posSurf[{poly.PosSurface}]=0x{surfaceId:X8} SURFACE-MISS"
                    : $"posSurf[{poly.PosSurface}]=0x{surfaceId:X8} type={surface.Type} origTex=0x{(uint)surface.OrigTextureId:X8}";
            }

            _out.WriteLine(
                $"  portal poly={p.PolygonId} -> 0x{p.OtherCellId:X4} [{p.Flags}] " +
                $"stip={poly.Stippling} sides={poly.SidesType} verts={poly.VertexIds.Count} " +
                $"n=({n.X:F2},{n.Y:F2},{n.Z:F2}) " +
                $"x=[{min.X:F2},{max.X:F2}] y=[{min.Y:F2},{max.Y:F2}] z=[{min.Z:F2},{max.Z:F2}] " +
                $"{surfInfo} DRAWN={drawn}");
        }

        int drawnCount = 0, missCount = 0;
        foreach (var (id, poly) in cs.Polygons)
        {
            if (!WouldDraw(poly, cell)) continue;
            drawnCount++;
            uint surfaceId = 0x08000000u | (uint)cell.Surfaces[poly.PosSurface];
            if (dats.Get<Surface>(surfaceId) is null)
            {
                missCount++;
                _out.WriteLine($"  >>> DRAWN poly {id} has MISSING surface 0x{surfaceId:X8}");
            }
        }
        _out.WriteLine($"  drawn-poly sweep: {drawnCount} drawn, {missCount} with missing surfaces");
    }

    [Theory]
    [InlineData(0x8A02016Eu, 0x8A02011Eu)]
    [InlineData(0x8A02016Eu, 0x8A02017Au)]
    [InlineData(0x8A020182u, 0x8A020183u)]
    public void ReciprocalPortalPolys_CoincidenceAndDrawVerdict(uint cellA, uint cellB)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var la = LoadCell(dats, cellA);
        var lb = LoadCell(dats, cellB);
        Assert.NotNull(la);
        Assert.NotNull(lb);
        var (ca, csa) = la!.Value;
        var (cb, csb) = lb!.Value;
        var wa = WorldTransform(ca);
        var wb = WorldTransform(cb);

        ushort lowA = (ushort)(cellA & 0xFFFFu);
        ushort lowB = (ushort)(cellB & 0xFFFFu);

        _out.WriteLine($"=== reciprocal pair 0x{cellA:X8} <-> 0x{cellB:X8} ===");
        foreach (var pa in ca.CellPortals)
        {
            if (pa.OtherCellId != lowB) continue;
            if (!csa.Polygons.TryGetValue((ushort)pa.PolygonId, out var polyA)) continue;
            var va = WorldVerts(csa, polyA, wa);
            if (va.Count < 3) continue;
            var na = Vector3.Normalize(Vector3.Cross(va[1] - va[0], va[2] - va[0]));
            float da = Vector3.Dot(na, va[0]);
            bool drawnA = WouldDraw(polyA, ca);

            _out.WriteLine($"  A poly={pa.PolygonId} [{pa.Flags}] n=({na.X:F2},{na.Y:F2},{na.Z:F2}) planeD={da:F2} " +
                           $"stip={polyA.Stippling} sides={polyA.SidesType} DRAWN={drawnA}");

            foreach (var pb in cb.CellPortals)
            {
                if (pb.OtherCellId != lowA) continue;
                if (!csb.Polygons.TryGetValue((ushort)pb.PolygonId, out var polyB)) continue;
                var vb = WorldVerts(csb, polyB, wb);
                if (vb.Count < 3) continue;
                var nb = Vector3.Normalize(Vector3.Cross(vb[1] - vb[0], vb[2] - vb[0]));
                float db = Vector3.Dot(nb, vb[0]);
                bool drawnB = WouldDraw(polyB, cb);

                float align = Vector3.Dot(na, nb);
                bool coincident = MathF.Abs(align) > 0.99f
                    && MathF.Abs(MathF.Abs(da) - MathF.Abs(db)) < 0.05f;

                _out.WriteLine($"    B poly={pb.PolygonId} [{pb.Flags}] n=({nb.X:F2},{nb.Y:F2},{nb.Z:F2}) planeD={db:F2} " +
                               $"stip={polyB.Stippling} sides={polyB.SidesType} DRAWN={drawnB} " +
                               $"align={align:F3} coincident={coincident}" +
                               (coincident && drawnA && drawnB ? "  >>> Z-FIGHT CANDIDATE (both drawn, same plane)" : ""));
            }
        }
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_CorridorNeighborhood_CoplanarOverlappingDrawnPolyPairs()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var cellIds = new HashSet<uint> { 0x8A020164u, 0x8A020165u, 0x8A02016Eu, 0x8A02017Au };
        foreach (var seed in new List<uint>(cellIds))
        {
            var seedCell = dats.Get<EnvCell>(seed);
            if (seedCell is null) continue;
            foreach (var p in seedCell.CellPortals)
                cellIds.Add(0x8A020000u | p.OtherCellId);
        }

        var drawn = new List<(uint CellId, ushort PolyId, Vector3 N, float D,
            Vector3 Min, Vector3 Max, uint SurfaceId)>();
        foreach (var cellId in cellIds)
        {
            var loaded = LoadCell(dats, cellId);
            if (loaded is null) continue;
            var (cell, cs) = loaded.Value;
            var world = WorldTransform(cell);

            foreach (var (id, poly) in cs.Polygons)
            {
                if (!WouldDraw(poly, cell)) continue;
                var w = WorldVerts(cs, poly, world);
                if (w.Count < 3) continue;
                var n = Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0]));
                float d = Vector3.Dot(n, w[0]);
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                foreach (var v in w) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                uint surfaceId = 0x08000000u | (uint)cell.Surfaces[poly.PosSurface];
                drawn.Add((cellId, id, n, d, min, max, surfaceId));
            }
        }
        _out.WriteLine($"cells={cellIds.Count} drawnPolys={drawn.Count}");

        int pairs = 0;
        for (int i = 0; i < drawn.Count; i++)
        {
            for (int j = i + 1; j < drawn.Count; j++)
            {
                var a = drawn[i]; var b = drawn[j];
                if (a.CellId == b.CellId && a.PolyId == b.PolyId) continue;

                float align = Vector3.Dot(a.N, b.N);
                if (MathF.Abs(align) < 0.999f) continue;
                float dB = align > 0 ? b.D : -b.D;
                if (MathF.Abs(a.D - dB) > 0.02f) continue;   // same plane within 2 cm

                // Overlap in world AABB, with meaningful area in the plane.
                float ox = MathF.Min(a.Max.X, b.Max.X) - MathF.Max(a.Min.X, b.Min.X);
                float oy = MathF.Min(a.Max.Y, b.Max.Y) - MathF.Max(a.Min.Y, b.Min.Y);
                float oz = MathF.Min(a.Max.Z, b.Max.Z) - MathF.Max(a.Min.Z, b.Min.Z);
                if (ox < 0.05f || oy < 0.05f) continue;
                // For horizontal planes require XY overlap area; for walls allow thin Z.
                bool horizontal = MathF.Abs(a.N.Z) > 0.85f;
                if (horizontal && ox * oy < 0.05f) continue;
                if (!horizontal && oz < 0.05f) continue;

                pairs++;
                _out.WriteLine(
                    $">>> COPLANAR-OVERLAP {(a.CellId == b.CellId ? "SAME-CELL" : "CROSS-CELL")}: " +
                    $"0x{a.CellId:X8} poly {a.PolyId} (surf 0x{a.SurfaceId:X8}) <-> " +
                    $"0x{b.CellId:X8} poly {b.PolyId} (surf 0x{b.SurfaceId:X8}) " +
                    $"n=({a.N.X:F2},{a.N.Y:F2},{a.N.Z:F2}) align={align:F3} " +
                    $"overlap x={ox:F2} y={oy:F2} z=[{MathF.Max(a.Min.Z, b.Min.Z):F2}..{MathF.Min(a.Max.Z, b.Max.Z):F2}]");
            }
        }
        _out.WriteLine($"coplanar overlapping drawn pairs: {pairs}");
    }

    [Theory]
    [InlineData(0x08000377u)]
    [InlineData(0x08000376u)]
    [InlineData(0x08000375u)]
    [InlineData(0x08000378u)]
    [InlineData(0x08000379u)]   // under-level walls
    [InlineData(0x080000DFu)]
    public void FloorSurface_DecodedAlphaHistogram(uint surfaceId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var surface = dats.Get<Surface>(surfaceId);
        Assert.NotNull(surface);
        _out.WriteLine($"Surface 0x{surfaceId:X8}: type={surface!.Type} origTex=0x{(uint)surface.OrigTextureId:X8} " +
                       $"transl={surface.Translucency:F2}");
        if (surface.OrigTextureId == 0) { _out.WriteLine("  (no texture — solid color surface)"); return; }

        var surfTex = dats.Get<SurfaceTexture>((uint)surface.OrigTextureId);
        Assert.NotNull(surfTex);
        _out.WriteLine($"  SurfaceTexture 0x{(uint)surface.OrigTextureId:X8}: {surfTex!.Textures.Count} textures " +
                       $"[{string.Join(" ", surfTex.Textures.ConvertAll(t => $"0x{t:X8}"))}]");

        foreach (var texId in surfTex.Textures)
        {
            var rs = dats.Get<RenderSurface>((uint)texId);
            if (rs is null) { _out.WriteLine($"  RenderSurface 0x{texId:X8}: MISS"); continue; }

            var data = new byte[rs.Width * rs.Height * 4];
            bool decodedOk = true;
            switch (rs.Format)
            {
                case DatReaderWriter.Enums.PixelFormat.PFID_INDEX16:
                {
                    var pal = dats.Get<Palette>(rs.DefaultPaletteId);
                    if (pal is null) { _out.WriteLine($"  RenderSurface 0x{texId:X8}: INDEX16 with no palette 0x{rs.DefaultPaletteId:X8}"); decodedOk = false; break; }
                    AcDream.Core.Rendering.Wb.TextureHelpers.FillIndex16(rs.SourceData, pal, data, rs.Width, rs.Height);
                    break;
                }
                case DatReaderWriter.Enums.PixelFormat.PFID_P8:
                {
                    var pal = dats.Get<Palette>(rs.DefaultPaletteId);
                    if (pal is null) { _out.WriteLine($"  RenderSurface 0x{texId:X8}: P8 with no palette"); decodedOk = false; break; }
                    AcDream.Core.Rendering.Wb.TextureHelpers.FillP8(rs.SourceData, pal, data, rs.Width, rs.Height);
                    break;
                }
                case DatReaderWriter.Enums.PixelFormat.PFID_R5G6B5:
                    AcDream.Core.Rendering.Wb.TextureHelpers.FillR5G6B5(rs.SourceData, data, rs.Width, rs.Height);
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_A4R4G4B4:
                    AcDream.Core.Rendering.Wb.TextureHelpers.FillA4R4G4B4(rs.SourceData, data, rs.Width, rs.Height);
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_A8R8G8B8:
                    AcDream.Core.Rendering.Wb.TextureHelpers.FillA8R8G8B8(rs.SourceData, data, rs.Width, rs.Height);
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_R8G8B8:
                    AcDream.Core.Rendering.Wb.TextureHelpers.FillR8G8B8(rs.SourceData, data, rs.Width, rs.Height);
                    break;
                case DatReaderWriter.Enums.PixelFormat.PFID_DXT1:
                {
                    int blocks = rs.SourceData.Length / 8;
                    int threeColorBlocks = 0;
                    long transparentTexels = 0;
                    for (int b = 0; b < blocks; b++)
                    {
                        int o = b * 8;
                        ushort c0 = (ushort)(rs.SourceData[o] | (rs.SourceData[o + 1] << 8));
                        ushort c1 = (ushort)(rs.SourceData[o + 2] | (rs.SourceData[o + 3] << 8));
                        if (c0 > c1) continue;
                        threeColorBlocks++;
                        for (int bi = 0; bi < 4; bi++)
                        {
                            byte row = rs.SourceData[o + 4 + bi];
                            for (int t = 0; t < 4; t++)
                                if (((row >> (t * 2)) & 0x3) == 3)
                                    transparentTexels++;
                        }
                    }
                    _out.WriteLine($"  RenderSurface 0x{texId:X8} DXT1 {rs.Width}x{rs.Height}: blocks={blocks} " +
                                   $"threeColorBlocks={threeColorBlocks} alpha0Texels={transparentTexels}" +
                                   (transparentTexels > 0
                                       ? "  >>> ALPHA=0 TEXELS PRESENT (opaque-pass discard punches holes)"
                                       : "  (no transparent-mode texels)"));
                    decodedOk = false;   // histogram printed above; skip the RGBA path
                    break;
                }
                default:
                    _out.WriteLine($"  RenderSurface 0x{texId:X8}: fmt={rs.Format} (not decoded by this test)");
                    decodedOk = false;
                    break;
            }
            if (!decodedOk) continue;

            // Alpha histogram over the decoded RGBA bytes (stride 4, alpha at +3).
            int n = data.Length / 4;
            int a255 = 0, aHigh = 0, aMid = 0, aLow = 0, a0 = 0;
            byte minA = 255;
            for (int i = 0; i < n; i++)
            {
                byte a = data[i * 4 + 3];
                if (a < minA) minA = a;
                if (a == 255) a255++;
                else if (a >= 243) aHigh++;      // ≥0.95 — safe for A2C
                else if (a >= 13) aMid++;
                else if (a > 0) aLow++;
                else a0++;
            }
            _out.WriteLine($"  RenderSurface 0x{texId:X8} fmt={rs.Format} {rs.Width}x{rs.Height}: " +
                           $"alpha histogram n={n} a=255:{a255} 243-254:{aHigh} 13-242:{aMid} 1-12:{aLow} 0:{a0} minA={minA}" +
                           (aMid + aLow + a0 > 0 ? "  >>> SUB-OPAQUE ALPHA PRESENT (A2C hole candidate)" : "  (fully opaque)"));
        }
    }

    [Theory]
    [InlineData(0x8A02011Eu)]
    [InlineData(0x8A020119u)]
    [InlineData(0x8A02011Du)]
    [InlineData(0x8A020122u)]
    [InlineData(0x8A02011Fu)]
    [InlineData(0x8A02016Eu)]
    [InlineData(0x8A02017Au)]
    [InlineData(0x8A020165u)]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_UnderHall_DrawnPolys_SurfaceColors(uint cellId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var loaded = LoadCell(dats, cellId);
        if (loaded is null) { _out.WriteLine($"0x{cellId:X8} NOT FOUND"); return; }
        var (cell, cs) = loaded.Value;
        var world = WorldTransform(cell);

        _out.WriteLine($"=== 0x{cellId:X8} drawn polys ===");
        foreach (var (id, poly) in cs.Polygons)
        {
            if (!WouldDraw(poly, cell)) continue;
            var w = WorldVerts(cs, poly, world);
            if (w.Count < 3) continue;
            var n = Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0]));
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in w) { minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z); }

            uint surfaceId = 0x08000000u | (uint)cell.Surfaces[poly.PosSurface];
            var surface = dats.Get<Surface>(surfaceId);
            string surfInfo = surface is null
                ? $"0x{surfaceId:X8} MISS"
                : $"0x{surfaceId:X8} type={surface.Type} color=0x{surface.ColorValue:X8} origTex=0x{(uint)surface.OrigTextureId:X8} lum={surface.Luminosity:F2} transl={surface.Translucency:F2}";
            _out.WriteLine($"  poly {id}: n=({n.X:F2},{n.Y:F2},{n.Z:F2}) z=[{minZ:F2},{maxZ:F2}] " +
                           $"verts={poly.VertexIds.Count} surf={surfInfo}");
        }
    }

    [Theory]
    [InlineData(0x8A02016Eu, 0x8A02017Au, 85.00f, -40f, -5.0f)]
    [InlineData(0x8A020182u, 0x8A020183u, 98.333f, -40f, -7.5f)]
    public void SeamCells_CellBspContainment_AcrossPortalPlane(
        uint cellAId, uint cellBId, float planeX, float y, float z)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var la = LoadCell(dats, cellAId);
        var lb = LoadCell(dats, cellBId);
        Assert.NotNull(la);
        Assert.NotNull(lb);
        var (ca, csa) = la!.Value;
        var (cb, csb) = lb!.Value;

        Matrix4x4.Invert(WorldTransform(ca), out var invA);
        Matrix4x4.Invert(WorldTransform(cb), out var invB);

        _out.WriteLine($"=== CellBSP containment across plane x={planeX:F2} " +
                       $"(A=0x{cellAId:X8}, B=0x{cellBId:X8}) ===");
        for (float dx = -0.6f; dx <= 0.65f; dx += 0.05f)
        {
            var world = new Vector3(planeX + dx, y, z);
            bool inA = csa.CellBSP?.Root is not null
                && Physics.PointInCellBspViaBspQuery(csa.CellBSP.Root, Vector3.Transform(world, invA));
            bool inB = csb.CellBSP?.Root is not null
                && Physics.PointInCellBspViaBspQuery(csb.CellBSP.Root, Vector3.Transform(world, invB));
            _out.WriteLine($"  x=plane{(dx >= 0 ? "+" : "")}{dx:F2} inA={(inA ? "Y" : "-")} inB={(inB ? "Y" : "-")}" +
                           (inA && inB ? "  <<< OVERLAP" : !inA && !inB ? "  <<< NEITHER" : ""));
        }
    }

    private static class Physics
    {
        public static bool PointInCellBspViaBspQuery(
            DatReaderWriter.Types.CellBSPNode node, Vector3 localPoint)
            => AcDream.Core.Physics.BSPQuery.PointInsideCellBsp(node, localPoint);
    }

    [Theory]
    [InlineData(0x8A020182u)]
    [InlineData(0x8A020183u)]
    public void StairTransit_GeometryOwnerAndPortalOrientation(uint cellId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var loaded = LoadCell(dats, cellId);
        Assert.NotNull(loaded);
        var (cell, cs) = loaded!.Value;
        var world = WorldTransform(cell);

        int floors = 0, ceilings = 0, walls = 0, inclined = 0;
        var floorZLevels = new SortedDictionary<int, int>();

        foreach (var (id, poly) in cs.Polygons)
        {
            var w = WorldVerts(cs, poly, world);
            if (w.Count < 3) continue;
            var n = Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0]));
            float az = MathF.Abs(n.Z);
            if (az > 0.85f)
            {
                // Horizontal poly — bucket by mean z.
                float meanZ = 0; foreach (var v in w) meanZ += v.Z; meanZ /= w.Count;
                int zKey = (int)MathF.Round(meanZ * 10f);
                floorZLevels.TryGetValue(zKey, out var c);
                floorZLevels[zKey] = c + 1;
                if (n.Z > 0) floors++; else ceilings++;
            }
            else if (az < 0.25f) walls++;
            else
            {
                inclined++;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var v in w) { minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z); }
                _out.WriteLine($"  INCLINED poly {id}: n=({n.X:F2},{n.Y:F2},{n.Z:F2}) z=[{minZ:F2},{maxZ:F2}] " +
                               $"verts={poly.VertexIds.Count} stip={poly.Stippling} DRAWN={WouldDraw(poly, cell)}");
            }
        }

        _out.WriteLine($"=== 0x{cellId:X8} poly histogram: floors={floors} ceilings={ceilings} walls={walls} inclined={inclined} ===");
        _out.WriteLine("  horizontal-poly z-ladder (z → count): " +
                       string.Join(" ", System.Linq.Enumerable.Select(floorZLevels, kv => $"{kv.Key / 10f:F1}:{kv.Value}")));

        foreach (var p in cell.CellPortals)
        {
            if (!cs.Polygons.TryGetValue((ushort)p.PolygonId, out var poly)) continue;
            var w = WorldVerts(cs, poly, world);
            if (w.Count < 3) continue;
            var n = Vector3.Normalize(Vector3.Cross(w[1] - w[0], w[2] - w[0]));
            string orient = MathF.Abs(n.Z) > 0.85f ? "HORIZONTAL (floor/ceiling portal)"
                          : MathF.Abs(n.Z) < 0.25f ? "vertical (wall portal)"
                          : "INCLINED portal";
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var v in w) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            _out.WriteLine($"  portal poly={p.PolygonId} -> 0x{p.OtherCellId:X4} [{p.Flags}] {orient} " +
                           $"n=({n.X:F2},{n.Y:F2},{n.Z:F2}) z=[{min.Z:F2},{max.Z:F2}]");
        }
    }

    [Theory]
    [InlineData(0x8A02015Eu)]
    [InlineData(0x8A02016Eu)]
    [Trait("Purpose", "Diagnostic")]
    public void CellVertexNormals_SmoothOrFaceted_Dump(uint cellId)
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: no dat dir"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var loaded = LoadCell(dats, cellId);
        if (loaded is null) { _out.WriteLine($"0x{cellId:X8} NOT FOUND"); return; }
        var (cell, cs) = loaded.Value;

        int zero = 0, faceMatch = 0, smooth = 0, total = 0;
        _out.WriteLine($"=== 0x{cellId:X8} per-vertex dat normals (local space), DRAWN polys ===");
        foreach (var (id, poly) in cs.Polygons)
        {
            if (!WouldDraw(poly, cell)) continue;
            // Geometric face normal from the first 3 LOCAL verts.
            var lv = new List<Vector3>();
            var vn = new List<Vector3>();
            foreach (var vid in poly.VertexIds)
                if (cs.VertexArray.Vertices.TryGetValue((ushort)vid, out var v)) { lv.Add(v.Origin); vn.Add(v.Normal); }
            if (lv.Count < 3) continue;
            total++;
            var face = Vector3.Normalize(Vector3.Cross(lv[1] - lv[0], lv[2] - lv[0]));

            bool allZero = vn.TrueForAll(x => x == Vector3.Zero);
            bool allEqualFace = vn.TrueForAll(x => x != Vector3.Zero && Vector3.Dot(Vector3.Normalize(x), face) > 0.999f);
            bool varying = false;
            for (int i = 1; i < vn.Count; i++)
                if (vn[i] != Vector3.Zero && Vector3.Dot(Vector3.Normalize(vn[i]), Vector3.Normalize(vn[0])) < 0.999f) varying = true;
            if (allZero) zero++; else if (allEqualFace) faceMatch++; else if (varying) smooth++;

            string tag = allZero ? "ZERO(→UnitZ)" : allEqualFace ? "FLAT(=face)" : varying ? "SMOOTH(varies)" : "uniform(≠face)";
            _out.WriteLine($"  poly {id} face=({face.X:F2},{face.Y:F2},{face.Z:F2}) {tag} datN=[" +
                           string.Join(" ", vn.ConvertAll(x => $"({x.X:F2},{x.Y:F2},{x.Z:F2})")) + "]");
        }
        _out.WriteLine($"SUMMARY 0x{cellId:X8}: drawnPolys={total} zero={zero} flatFace={faceMatch} smooth={smooth}");
    }
}
