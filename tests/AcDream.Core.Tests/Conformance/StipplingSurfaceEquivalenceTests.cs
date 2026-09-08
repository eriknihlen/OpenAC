using System;
using System.Collections.Generic;
using System.Linq;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public sealed class StipplingSurfaceEquivalenceTests
{
    private readonly ITestOutputHelper _out;
    public StipplingSurfaceEquivalenceTests(ITestOutputHelper output) => _out = output;

    private static readonly uint[] Landblocks =
    {
        0xA9B40000u,   // Holtburg town
        0xA9B30000u,
        0xAAB30000u,   // meeting hall block
        0xA9B50000u,
        0xAAB40000u,
    };

    [Fact]
    public void NoPosStippling_Equals_UntexturedSurface_OnBuildingsAndCells()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        int polysChecked = 0;
        var aViolations = new List<string>();   // NoPos but TEXTURED
        var bViolations = new List<string>();   // untextured but NOT NoPos

        bool IsTextured(DatReaderWriter.DBObjs.Surface? s) =>
            s is not null &&
            (s.Type.HasFlag(DatReaderWriter.Enums.SurfaceType.Base1Image)
             || s.Type.HasFlag(DatReaderWriter.Enums.SurfaceType.Base1ClipMap));

        // ---- building shell GfxObjs ----
        var buildingModels = new SortedSet<uint>();
        var environments = new SortedSet<uint>();
        foreach (uint lb in Landblocks)
        {
            var lbi = dats.Get<DatReaderWriter.DBObjs.LandBlockInfo>(lb | 0xFFFEu);
            if (lbi is null) continue;
            foreach (var b in lbi.Buildings ?? new()) buildingModels.Add(b.ModelId);
            for (uint low = 0x0100; low < 0x0100 + lbi.NumCells; low++)
            {
                var dc = dats.Get<DatReaderWriter.DBObjs.EnvCell>(lb | low);
                if (dc is not null) environments.Add(0x0D000000u | dc.EnvironmentId);
            }
        }
        Assert.NotEmpty(buildingModels);
        Assert.NotEmpty(environments);

        foreach (var mid in buildingModels)
        {
            var gfx = dats.Get<DatReaderWriter.DBObjs.GfxObj>(mid);
            if (gfx is null) continue;
            foreach (var kv in gfx.Polygons)
            {
                var poly = kv.Value;
                polysChecked++;
                bool noPos = poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.NoPos);
                DatReaderWriter.DBObjs.Surface? surf = null;
                if (poly.PosSurface >= 0 && poly.PosSurface < gfx.Surfaces.Count)
                    surf = dats.Get<DatReaderWriter.DBObjs.Surface>(gfx.Surfaces[poly.PosSurface]);
                if (noPos && IsTextured(surf))
                    aViolations.Add($"gfx 0x{mid:X8} poly {kv.Key}: NoPos but textured surface");
                if (!noPos && surf is not null && !IsTextured(surf))
                    bViolations.Add($"gfx 0x{mid:X8} poly {kv.Key}: textured-less surface without NoPos (type={surf.Type})");
            }
        }

        var cellPortalPolyMismatches = new List<string>();
        foreach (var envId in environments)
        {
            var env = dats.Get<DatReaderWriter.DBObjs.Environment>(envId);
            if (env is null) continue;
            foreach (var (csId, cs) in env.Cells)
            {
                foreach (var kv in cs.Polygons)
                {
                    var poly = kv.Value;
                    polysChecked++;
                    bool noPos = poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.NoPos);
                    if (noPos)
                    {
                        bool isPortalPoly = cs.Portals.Any(p => p == kv.Key);
                        if (!isPortalPoly)
                            cellPortalPolyMismatches.Add($"env 0x{envId:X8} struct {csId} poly {kv.Key}: NoPos but not a portal poly");
                    }
                }
            }
        }

        _out.WriteLine($"checked {polysChecked} polys across {buildingModels.Count} building models + {environments.Count} environments");
        _out.WriteLine($"(a) building NoPos-but-textured (load-bearing): {aViolations.Count}");
        foreach (var v in aViolations.Take(20)) _out.WriteLine($"   {v}");
        _out.WriteLine($"(b) building untextured-but-not-NoPos (load-bearing): {bViolations.Count}");
        foreach (var v in bViolations.Take(20)) _out.WriteLine($"   {v}");
        _out.WriteLine($"(c) cell NoPos-but-not-portal-poly (content-shape pin, not a draw rule): {cellPortalPolyMismatches.Count}");
        foreach (var v in cellPortalPolyMismatches.Take(20)) _out.WriteLine($"   {v}");
        Assert.Empty(cellPortalPolyMismatches);

        Assert.Empty(aViolations);
        Assert.Empty(bViolations);
    }
}
