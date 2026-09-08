using System.IO;
using System.Text;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Physics;

[Trait("Lane", "InstalledDat")]
public sealed class Issue147ArwicBuildingsDumpTests
{
    private readonly ITestOutputHelper _out;
    public Issue147ArwicBuildingsDumpTests(ITestOutputHelper o) => _out = o;

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void DumpArwicBuildingsAndObjects()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir)) { _out.WriteLine($"SKIP: no dats at {datDir}"); Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var info = dats.Get<LandBlockInfo>(0xC6A9FFFEu);   // Arwic landblock info
        Assert.NotNull(info);

        var sb = new StringBuilder();
        sb.AppendLine($"Arwic 0xC6A9FFFE: Buildings={info.Buildings.Count} Objects={info.Objects.Count}");

        int portalless = 0;
        sb.AppendLine("--- Buildings (ModelId, portals, origin) ---");
        foreach (var b in info.Buildings)
        {
            if (b.Portals.Count == 0) portalless++;
            sb.AppendLine(System.FormattableString.Invariant(
                $"  model=0x{b.ModelId:X8} portals={b.Portals.Count} origin=({b.Frame.Origin.X:F1},{b.Frame.Origin.Y:F1},{b.Frame.Origin.Z:F1})"));
        }
        sb.AppendLine($"--- portal-less buildings (skipped by the collision cache): {portalless} ---");

        sb.AppendLine("--- Objects / stabs (first 50: Id, origin) ---");
        int n = 0;
        foreach (var o in info.Objects)
        {
            if (n++ >= 50) break;
            sb.AppendLine(System.FormattableString.Invariant(
                $"  id=0x{o.Id:X8} origin=({o.Frame.Origin.X:F1},{o.Frame.Origin.Y:F1},{o.Frame.Origin.Z:F1})"));
        }

        var outPath = Path.Combine(Path.GetTempPath(), "arwic-buildings-dump.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(also written to {outPath})");
    }
}
