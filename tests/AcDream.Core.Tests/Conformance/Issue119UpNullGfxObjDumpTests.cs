using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Meshing;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public sealed class Issue119UpNullGfxObjDumpTests
{
    private readonly ITestOutputHelper _out;
    public Issue119UpNullGfxObjDumpTests(ITestOutputHelper output) => _out = output;

    public static readonly TheoryData<uint> UpNullIds = new() { 0x010002B4u, 0x010008A8u };

    [Theory]
    [MemberData(nameof(UpNullIds))]
    [Trait("Purpose", "Diagnostic")]
    public void DumpUpNullGfxObj(uint id)
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.True(dats.Portal.TryGet<GfxObj>(id, out var gfx) && gfx is not null,
            $"GfxObj 0x{id:X8} not in portal dat");

        _out.WriteLine($"=== GfxObj 0x{id:X8} ===");
        _out.WriteLine($"Flags={gfx!.Flags} Polygons={gfx.Polygons.Count} Vertices={gfx.VertexArray.Vertices.Count} Surfaces={gfx.Surfaces.Count}");
        _out.WriteLine($"DrawingBSP={(gfx.DrawingBSP?.Root is null ? "NONE" : "present")} PhysicsBSP={(gfx.PhysicsBSP?.Root is null ? "NONE" : "present")}");

        for (int i = 0; i < gfx.Surfaces.Count; i++)
        {
            uint sid = gfx.Surfaces[i];
            string stype = dats.Portal.TryGet<Surface>(sid, out var surf) && surf is not null
                ? surf.Type.ToString()
                : "MISSING";
            _out.WriteLine($"  surface[{i}] = 0x{sid:X8} type={stype}");
        }

        int wouldAddPos = 0, wouldAddNeg = 0, degenerate = 0;
        var gateHistogram = new Dictionary<string, int>();
        foreach (var (pid, poly) in gfx.Polygons.OrderBy(kv => kv.Key))
        {
            string gate;
            if (poly.VertexIds.Count < 3) { degenerate++; gate = "degenerate(<3 verts)"; }
            else
            {
                bool pos = poly.PosSurface >= 0 && poly.PosSurface < gfx.Surfaces.Count;
                bool neg = (poly.Stippling.HasFlag(StipplingType.Negative)
                            || poly.Stippling.HasFlag(StipplingType.Both)
                            || (!poly.Stippling.HasFlag(StipplingType.NoNeg) && poly.SidesType == CullMode.Clockwise))
                    && poly.NegSurface >= 0 && poly.NegSurface < gfx.Surfaces.Count;
                if (pos) wouldAddPos++;
                if (neg) wouldAddNeg++;
                gate = pos || neg ? "DRAWS" : $"DROPPED stip={poly.Stippling} sides={poly.SidesType} posSurf={poly.PosSurface} negSurf={poly.NegSurface}";
            }
            gateHistogram.TryGetValue(gate, out int c);
            gateHistogram[gate] = c + 1;
            _out.WriteLine(FormattableString.Invariant(
                $"  poly[{pid}] verts={poly.VertexIds.Count} stip={poly.Stippling} sides={poly.SidesType} posSurf={poly.PosSurface} negSurf={poly.NegSurface} gate={gate}"));
        }

        _out.WriteLine($"--- summary: wouldAddPos={wouldAddPos} wouldAddNeg={wouldAddNeg} degenerate={degenerate} of {gfx.Polygons.Count} polys ---");
        foreach (var (gate, count) in gateHistogram.OrderByDescending(kv => kv.Value))
            _out.WriteLine($"  {count,3} × {gate}");
    }

    [Theory]
    [InlineData(0x010014C3u)]
    public void ShellModel_NoTexturedPolyIsDropped(uint id)
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.True(dats.Portal.TryGet<GfxObj>(id, out var gfx) && gfx is not null,
            $"GfxObj 0x{id:X8} not in portal dat");

        bool SurfaceIsTextured(short idx)
        {
            if (idx < 0 || idx >= gfx!.Surfaces.Count) return false;
            if (!dats.Portal.TryGet<Surface>(gfx.Surfaces[idx], out var surf) || surf is null) return false;
            return !RetailUntexturedSurfacePolicy.IsUntextured(surf.Type);
        }

        int draws = 0;
        var droppedTextured = new List<string>();
        foreach (var (pid, poly) in gfx!.Polygons.OrderBy(kv => kv.Key))
        {
            if (poly.VertexIds.Count < 3) continue;
            bool pos = poly.PosSurface >= 0 && poly.PosSurface < gfx.Surfaces.Count;
            bool neg = (poly.Stippling.HasFlag(StipplingType.Negative)
                        || poly.Stippling.HasFlag(StipplingType.Both)
                        || (!poly.Stippling.HasFlag(StipplingType.NoNeg) && poly.SidesType == CullMode.Clockwise))
                && poly.NegSurface >= 0 && poly.NegSurface < gfx.Surfaces.Count;
            if (pos || neg) { draws++; continue; }

            if (SurfaceIsTextured(poly.PosSurface) || SurfaceIsTextured(poly.NegSurface))
                droppedTextured.Add(FormattableString.Invariant(
                    $"poly[{pid}] stip={poly.Stippling} sides={poly.SidesType} posSurf={poly.PosSurface} negSurf={poly.NegSurface}"));
        }

        _out.WriteLine($"GfxObj 0x{id:X8}: polys={gfx.Polygons.Count} drawnByGates={draws} droppedTextured={droppedTextured.Count}");
        foreach (var line in droppedTextured)
            _out.WriteLine($"  {line}");
        Assert.True(droppedTextured.Count == 0,
            $"{droppedTextured.Count} textured polys are dropped by the extraction gates on 0x{id:X8} — see output");
    }
}
