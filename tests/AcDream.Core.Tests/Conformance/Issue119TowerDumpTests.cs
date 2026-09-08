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
public sealed class Issue119TowerDumpTests
{
    private readonly ITestOutputHelper _out;
    public Issue119TowerDumpTests(ITestOutputHelper output) => _out = output;

    private const uint Landblock = 0xAAB30000u;
    private const uint EnvironmentFilePrefix = 0x0D000000u;
    private static readonly Vector3 TowerSpot = new(107.4f, 59.0f, 112.0f); // AAB3-local, from the [snap]

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpTowerCellNeighbourhood()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var lbi = dats.Get<DatLandBlockInfo>(Landblock | 0xFFFEu);
        Assert.NotNull(lbi);
        _out.WriteLine($"=== LandBlockInfo 0x{Landblock | 0xFFFEu:X8}: NumCells={lbi!.NumCells}, Buildings={lbi.Buildings?.Count ?? 0} ===");
        if (lbi.Buildings is not null)
            for (int b = 0; b < lbi.Buildings.Count; b++)
            {
                var bld = lbi.Buildings[b];
                _out.WriteLine(FormattableString.Invariant(
                    $"  building[{b}] model=0x{bld.ModelId:X8} origin=({bld.Frame.Origin.X:F1},{bld.Frame.Origin.Y:F1},{bld.Frame.Origin.Z:F1}) portals={bld.Portals?.Count ?? 0}"));
                if (bld.Portals is not null)
                    foreach (var bp in bld.Portals)
                        _out.WriteLine(FormattableString.Invariant(
                            $"    bldPortal otherCell=0x{bp.OtherCellId:X4} stabs=[{string.Join(",", (bp.StabList ?? new List<ushort>()).Select(s => $"0x{s:X4}"))}]"));
            }

    foreach (uint low in EnumerateCells(lbi.NumCells))
        {
            uint id = Landblock | low;
            var dc = dats.Get<DatEnvCell>(id);
            if (dc is null) continue;

            var env = dats.Get<DatEnvironment>(EnvironmentFilePrefix | dc.EnvironmentId);
            if (env is null || !env.Cells.TryGetValue(dc.CellStructure, out var cs) || cs is null)
            { _out.WriteLine($"cell 0x{id:X8}: env 0x{dc.EnvironmentId:X} struct {dc.CellStructure} MISSING"); continue; }

            var world =
                Matrix4x4.CreateFromQuaternion(dc.Position.Orientation) *
                Matrix4x4.CreateTranslation(dc.Position.Origin);

            // bounds + distance to the user's spot
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var kvp in cs.VertexArray.Vertices)
            {
                var p = Vector3.Transform(new Vector3(kvp.Value.Origin.X, kvp.Value.Origin.Y, kvp.Value.Origin.Z), world);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var center = (min + max) * 0.5f;
            float dist = Vector2.Distance(new Vector2(center.X, center.Y), new Vector2(TowerSpot.X, TowerSpot.Y));
            bool near = dist < 18f;
            if (!near) continue;   // only the tower neighbourhood

            // stair signature (Issue113 discriminator)
            var flatHeights = new HashSet<int>();
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
                // Newell normal
                var n = Vector3.Zero;
                for (int i = 0; i < pts.Count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    n.X += (a.Y - b.Y) * (a.Z + b.Z);
                    n.Y += (a.Z - b.Z) * (a.X + b.X);
                    n.Z += (a.X - b.X) * (a.Y + b.Y);
                }
                if (n.LengthSquared() < 1e-12f) continue;
                n = Vector3.Normalize(n);
                if (MathF.Abs(n.Z) > 0.9f)
                    flatHeights.Add((int)MathF.Round(pts.Average(p => p.Z) * 4f)); // 25 cm bins
                else if (MathF.Abs(n.Z) > 0.25f)
                    rampPolys++;
            }

            bool seenOutside = dc.Flags.HasFlag(DatReaderWriter.Enums.EnvCellFlags.SeenOutside);
            _out.WriteLine(FormattableString.Invariant(
                $"cell 0x{id:X8} env=0x{dc.EnvironmentId:X4}/{dc.CellStructure} pos=({dc.Position.Origin.X:F1},{dc.Position.Origin.Y:F1},{dc.Position.Origin.Z:F1}) aabb=z[{min.Z:F1}..{max.Z:F1}] distXY={dist:F1} seenOutside={seenOutside} flatLevels={flatHeights.Count} rampPolys={rampPolys} polys={cs.Polygons.Count}"));

            _out.WriteLine($"  portals: [{string.Join(" ", dc.CellPortals.Select(p => $"->0x{p.OtherCellId:X4}(poly{p.PolygonId},flags={(ushort)p.Flags})"))}]");
            if (dc.VisibleCells is not null && dc.VisibleCells.Count > 0)
                _out.WriteLine($"  visibleCells: [{string.Join(",", dc.VisibleCells.Select(v => $"0x{v:X4}"))}]");

            if (dc.StaticObjects is not null)
                foreach (var so in dc.StaticObjects)
                    _out.WriteLine(FormattableString.Invariant(
                        $"  static gfx=0x{so.Id:X8} at ({so.Frame.Origin.X:F2},{so.Frame.Origin.Y:F2},{so.Frame.Origin.Z:F2})"));
        }
    }

    private static IEnumerable<uint> EnumerateCells(uint numCells)
    {
        for (uint low = 0x0100u; low < 0x0100u + numCells; low++)
            yield return low;
    }

    [Theory]
    [Trait("Purpose", "Diagnostic")]
    [InlineData(0x020005D8u)]
    [InlineData(0x020003F2u)]
    public void DumpTowerStairSetups(uint setupId)
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("dats unavailable — skipped"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        Assert.True(dats.Portal.TryGet<DatReaderWriter.DBObjs.Setup>(setupId, out var setup) && setup is not null,
            $"Setup 0x{setupId:X8} not in portal dat");

        _out.WriteLine($"=== Setup 0x{setupId:X8}: parts={setup!.Parts.Count} ===");
        for (int i = 0; i < setup.Parts.Count; i++)
        {
            uint gfxId = setup.Parts[i];
            string polyInfo = "MISSING";
            if (dats.Portal.TryGet<DatReaderWriter.DBObjs.GfxObj>(gfxId, out var gfx) && gfx is not null)
            {
                int noDraw = 0;
                foreach (var poly in gfx.Polygons.Values)
                    if (poly.Stippling.HasFlag(DatReaderWriter.Enums.StipplingType.NoPos))
                        noDraw++;
                polyInfo = $"polys={gfx.Polygons.Count} noPos={noDraw} surfaces={gfx.Surfaces.Count}";
            }
            string frame = "";
            if (setup.PlacementFrames.Count > 0)
            {
                var pf = setup.PlacementFrames.Values.First();
                if (i < pf.Frames.Count)
                {
                    var f = pf.Frames[i];
                    frame = FormattableString.Invariant(
                        $" frame0=({f.Origin.X:F2},{f.Origin.Y:F2},{f.Origin.Z:F2})");
                }
            }
            _out.WriteLine($"  part[{i}] gfx=0x{gfxId:X8} {polyInfo}{frame}");
        }
    }
}
