using System;
using System.IO;
using System.Linq;
using AcDream.App.Rendering.Wb;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Rendering;

[Trait("Lane", "InstalledDat")]
public class DrawingBspFilterTests
{
    private readonly ITestOutputHelper _out;
    public DrawingBspFilterTests(ITestOutputHelper output) => _out = output;

    private static string? ResolveDatDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;
        var def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    [Fact]
    public void MeetingHall_OrphanStairPolys_AreExcludedFromDrawSet()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var hall = dats.Get<DatReaderWriter.DBObjs.GfxObj>(0x010014C3u)!;
        var drawn = ObjectMeshManager.CollectDrawingBspPolygonIds(hall);
        Assert.NotNull(drawn);

        // The two orphans (the invisible walkable stair-ramp) must be excluded;
        // everything else referenced.
        Assert.DoesNotContain((ushort)0, drawn!);
        Assert.DoesNotContain((ushort)1, drawn!);
        var orphans = hall.Polygons.Keys.Where(k => !drawn.Contains(k)).OrderBy(k => k).ToArray();
        _out.WriteLine($"hall orphans: [{string.Join(",", orphans)}]");
        Assert.Equal(new ushort[] { 0, 1 }, orphans);
    }

    [Fact]
    public void HillCottage_OrphanPolys_AreExcludedFromDrawSet()
    {
        var datDir = ResolveDatDir();
        if (datDir is null) { _out.WriteLine("SKIP: dats unavailable"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        var cottage = dats.Get<DatReaderWriter.DBObjs.GfxObj>(0x01000827u)!;
        var drawn = ObjectMeshManager.CollectDrawingBspPolygonIds(cottage);
        Assert.NotNull(drawn);

        var orphans = cottage.Polygons.Keys.Where(k => !drawn!.Contains(k)).OrderBy(k => k).ToArray();
        _out.WriteLine($"cottage orphans: [{string.Join(",", orphans)}]");
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7 }, orphans);
    }
}
