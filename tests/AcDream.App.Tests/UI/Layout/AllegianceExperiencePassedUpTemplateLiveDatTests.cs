using AcDream.App.UI.Layout;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

// OpenAC #48: the allegiance page's "experience passed up" text comes from
// the authored entry of the allegiance string table, with the number's
// digits grouped the way every number the game prints is.
[Trait("Lane", "InstalledDat")]
public sealed class AllegianceExperiencePassedUpTemplateLiveDatTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    [InstalledDatFact]
    public void ResolveTemplate_PlacesTheGroupedValueInTheAuthoredEntry()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var resolver = new DatStringResolver(dats);

        string? text = resolver.ResolveTemplate(
            0x23000001u,
            DatStringResolver.ComputeHash("ID_Allegiance_VassalExperiencePassedUp"),
            new Dictionary<uint, string>
            {
                [DatStringResolver.ComputeHash("VALUE")] = "1,500,000",
            });

        Assert.Equal("1,500,000", text);
    }
}
