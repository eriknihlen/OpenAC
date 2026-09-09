using System;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Conformance;

[Trait("Lane", "InstalledDat")]
public class CottageDoorwayCharacterizationTests
{
    private readonly ITestOutputHelper _out;
    public CottageDoorwayCharacterizationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Characterize_CottageNeighborhood_PrintStructure()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);

        for (uint low = 0x0140; low <= 0x017F; low++)
        {
            uint id = ConformanceDats.HoltburgLandblock | low;
            var cache = new PhysicsDataCache();
            try
            {
                var cell = ConformanceDats.LoadEnvCell(dats, cache, id);
                var phys = cache.GetCellStruct(id)!;
                var origin = Vector3.Transform(Vector3.Zero, phys.WorldTransform);
                bool exit = phys.Portals.Any(p => p.OtherCellId == 0xFFFFu);
                _out.WriteLine(
                    $"0x{id:X8}: origin=({origin.X,7:F2},{origin.Y,7:F2},{origin.Z,6:F2}) " +
                    $"seenOut={(cell.SeenOutside ? 1 : 0)} bsp={(cell.ContainmentBsp?.Root is not null ? 1 : 0)} " +
                    $"portals={phys.Portals.Count} exit={(exit ? 1 : 0)} stab={cell.StabList.Count} " +
                    $"dests=[{string.Join(",", phys.Portals.Select(p => $"0x{p.OtherCellId:X4}"))}]");
            }
            catch (Exception ex)
            {
                // Most ids in the range won't exist — that's expected; skip silently
                // unless it's an unexpected failure shape.
                if (ex is not InvalidOperationException)
                    _out.WriteLine($"0x{id:X8}: ERROR {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public const uint Vestibule0170 = 0xA9B40170u;
    public const uint Room0171      = 0xA9B40171u;
    public const uint OutdoorLandcell0031 = 0xA9B40031u;

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Characterize_Doorway_FindInteriorPoints()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        foreach (var id in new[] { Vestibule0170, Room0171 })
        {
            var cache = new PhysicsDataCache();
            var cell = ConformanceDats.LoadEnvCell(dats, cache, id);
            var phys = cache.GetCellStruct(id)!;
            _out.WriteLine($"0x{id:X8}: localBounds min=({cell.LocalBoundsMin.X:F2},{cell.LocalBoundsMin.Y:F2},{cell.LocalBoundsMin.Z:F2}) " +
                           $"max=({cell.LocalBoundsMax.X:F2},{cell.LocalBoundsMax.Y:F2},{cell.LocalBoundsMax.Z:F2})");

            var min = cell.LocalBoundsMin; var max = cell.LocalBoundsMax;
            int inside = 0; Vector3? firstInsideLocal = null;
            for (int ix = 1; ix <= 5; ix++)
            for (int iy = 1; iy <= 5; iy++)
            for (int iz = 1; iz <= 5; iz++)
            {
                var local = new Vector3(
                    min.X + (max.X - min.X) * ix / 6f,
                    min.Y + (max.Y - min.Y) * iy / 6f,
                    min.Z + (max.Z - min.Z) * iz / 6f);
                var world = Vector3.Transform(local, phys.WorldTransform);
                if (cell.PointInCell(world)) { inside++; firstInsideLocal ??= local; }
            }
            _out.WriteLine($"  insidePoints={inside}/125 firstInsideLocal=" +
                (firstInsideLocal is { } p
                    ? $"({p.X:F3},{p.Y:F3},{p.Z:F3}) world={Vector3.Transform(p, phys.WorldTransform):F3}"
                    : "NONE"));
        }
    }


    public static readonly Vector3 Interior0170Local = new(5.865f, -8.449f, 0.417f);
    public static readonly Vector3 Interior0171Local = new(6.55f, -3.25f, 4.60f); // bsphere origin

    [Fact]
    public void Doorway_Topology_IsPinned()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();

        var vestibule = ConformanceDats.LoadEnvCell(dats, cache, Vestibule0170);
        var room = ConformanceDats.LoadEnvCell(dats, cache, Room0171);
        var vPhys = cache.GetCellStruct(Vestibule0170)!;
        var rPhys = cache.GetCellStruct(Room0171)!;

        Assert.True(vestibule.ContainmentBsp?.Root is not null, "vestibule must have a real BSP");
        Assert.True(room.ContainmentBsp?.Root is not null, "room must have a real BSP");
        Assert.True(vestibule.SeenOutside);
        Assert.True(room.SeenOutside);

        // Vestibule 0170: exit portal (0xFFFF) + portal to room 0171.
        Assert.Contains(vPhys.Portals, p => p.OtherCellId == 0xFFFFu);
        Assert.Contains(vPhys.Portals, p => p.OtherCellId == 0x0171u);
        // Room 0171: portals to vestibule + the two side rooms; NO exit portal.
        Assert.Contains(rPhys.Portals, p => p.OtherCellId == 0x0170u);
        Assert.DoesNotContain(rPhys.Portals, p => p.OtherCellId == 0xFFFFu);
    }

    [Fact]
    public void Doorway_InteriorPoints_ArePinned()
    {
        var datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var cache = new PhysicsDataCache();

        var vestibule = ConformanceDats.LoadEnvCell(dats, cache, Vestibule0170);
        var room = ConformanceDats.LoadEnvCell(dats, cache, Room0171);
        var vWorld = Vector3.Transform(Interior0170Local, cache.GetCellStruct(Vestibule0170)!.WorldTransform);
        var rWorld = Vector3.Transform(Interior0171Local, cache.GetCellStruct(Room0171)!.WorldTransform);

        Assert.True(vestibule.PointInCell(vWorld), $"pinned vestibule interior {vWorld} must be inside 0170");
        Assert.True(room.PointInCell(rWorld), $"pinned room interior {rWorld} must be inside 0171");
        Assert.False(vestibule.PointInCell(rWorld), "room interior must not be inside the vestibule");
        Assert.False(room.PointInCell(new Vector3(10000f, 10000f, 10000f)), "10km-away point cannot be inside");
    }
}
