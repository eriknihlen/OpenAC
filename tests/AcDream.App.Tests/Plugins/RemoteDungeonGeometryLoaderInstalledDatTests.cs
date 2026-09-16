using AcDream.App.Plugins;
using AcDream.App.Tests.Rendering;
using AcDream.Content;
using AcDream.DrakBot.Remote;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Plugins;

[Trait("Lane", "InstalledDat")]
public sealed class RemoteDungeonGeometryLoaderInstalledDatTests(ITestOutputHelper output)
{
    /// <summary>A five-storey dungeon the bot patrols; its cells sit at 0, -6, -12, -18 and -24 m.</summary>
    private const uint Landblock = 0x61450000u;

    [Fact]
    public void ADungeonReadsAsFloorsAndWalls_AndDrawsOneFloorPlanPerStorey()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        RemoteDungeonGeometry? geometry = RemoteDungeonGeometryLoader.Load(adapter, Landblock);
        Assert.NotNull(geometry);
        Assert.Equal(Landblock, geometry.LandblockId);
        int floors = geometry.Polygons.Count(p => p.Kind == RemoteDungeonSurface.Floor);
        int walls = geometry.Polygons.Count(p => p.Kind == RemoteDungeonSurface.Wall);
        output.WriteLine($"{geometry.Polygons.Count} polygons: {floors} floors, {geometry.Polygons.Count(p => p.Kind == RemoteDungeonSurface.Ramp)} ramps, {walls} walls");
        // A dungeon is mostly wall by polygon count and has floor to stand on; a
        // winding read the wrong way round would file every floor as a ceiling.
        Assert.True(floors > 100, $"expected the dungeon's floors, read {floors}");
        Assert.True(walls > 100, $"expected the dungeon's walls, read {walls}");

        // The frame is the status document's: the character stood at map
        // (-23.81, -47.46) when the bot dumped this dungeon, so metres
        // (-5715, -11390) lie inside it.
        float minX = geometry.Polygons.Min(p => p.Points.Min(q => q.X));
        float maxX = geometry.Polygons.Max(p => p.Points.Max(q => q.X));
        float minY = geometry.Polygons.Min(p => p.Points.Min(q => q.Y));
        float maxY = geometry.Polygons.Max(p => p.Points.Max(q => q.Y));
        output.WriteLine($"x {minX:F1}..{maxX:F1}  y {minY:F1}..{maxY:F1}  z {geometry.Polygons.Min(p => p.ZMin):F1}..{geometry.Polygons.Max(p => p.ZMax):F1}");
        Assert.InRange(-5715f, minX, maxX);
        Assert.InRange(-11390f, minY, maxY);

        foreach (var bin in geometry.Polygons.Where(p => p.Kind != RemoteDungeonSurface.Wall)
            .GroupBy(p => (int)Math.Round((p.ZMin + p.ZMax) / 2d)).OrderBy(g => g.Key))
        {
            output.WriteLine($"z {bin.Key,4}: {bin.Sum(p => RemoteDungeonMapRasterizer.Area(p.Points)),8:F1} m2 in {bin.Count()} polygons");
        }
        RemoteDungeonMaps? maps = RemoteDungeonMapRasterizer.Render(geometry, DateTime.UnixEpoch);
        Assert.NotNull(maps);
        output.WriteLine($"{maps.Layers.Count} layers, {maps.Layers[0].Width}x{maps.Layers[0].Height} at ({maps.Layers[0].XMin}, {maps.Layers[0].YMin}): z {string.Join(", ", maps.Layers.Select(l => l.Z))}");
        Assert.Equal(5, maps.Layers.Count);
        Assert.Equal([-24d, -18d, -12d, -6d, 0d], maps.Layers.Select(l => l.Z));

        string? dumpDir = Environment.GetEnvironmentVariable("ACDREAM_MAP_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(dumpDir))
        {
            Directory.CreateDirectory(dumpDir);
            foreach (RemoteDungeonMapLayer layer in maps.Layers)
                File.WriteAllBytes(Path.Combine(dumpDir, $"{Landblock >> 16:X4}-{layer.Layer}.png"), layer.Png);
        }
    }
}
