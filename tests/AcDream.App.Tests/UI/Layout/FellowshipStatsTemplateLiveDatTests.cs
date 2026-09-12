using AcDream.App.UI.Layout;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

// OpenAC #38: the fellowship row's "level/share%" text comes from the
// authored entry of the fellowship string table.
[Trait("Lane", "InstalledDat")]
public sealed class FellowshipStatsTemplateLiveDatTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    [InstalledDatFact]
    public void ResolveTemplate_ComposesLevelSlashSharePercent()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var resolver = new DatStringResolver(dats);

        string? text = resolver.ResolveTemplate(
            0x23000001u,
            0x003B5A03u,
            new Dictionary<uint, string> { [5286556u] = "12", [174673717u] = "31" });

        Assert.Equal("12/31%", text);
    }
}
