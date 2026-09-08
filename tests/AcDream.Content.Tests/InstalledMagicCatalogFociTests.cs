using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class InstalledMagicCatalogFociTests
{
    [Fact]
    public void FociMap_ResolvesEveryRetailSchoolToAcesFociWcid()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        MagicCatalog catalog = MagicCatalog.Load(adapter);

        Assert.Equal(15271u, catalog.MagicPackWcidForSchool(1u));
        Assert.Equal(15270u, catalog.MagicPackWcidForSchool(2u));
        Assert.Equal(15269u, catalog.MagicPackWcidForSchool(3u));
        Assert.Equal(15268u, catalog.MagicPackWcidForSchool(4u));
        Assert.Equal(43173u, catalog.MagicPackWcidForSchool(5u));
    }
}
