using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class CharacterTitleResolverLiveDatTests
{
    private static string DatDirectory =>
        System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    [InstalledDatFact]
    public void Resolve_PinsSeveralTitleIdsToTheirRetailDisplayStrings()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var resolver = new CharacterTitleResolver(new DatCollectionAdapter(dats));

        Assert.Null(resolver.Resolve(0u));

        Assert.Equal("Adventurer", resolver.Resolve(1u));
        Assert.Equal("Archer", resolver.Resolve(2u));
        Assert.Equal("Blademaster", resolver.Resolve(3u));
        Assert.Equal("Life Mage", resolver.Resolve(5u));
        Assert.Equal("War Mage", resolver.Resolve(13u));
        Assert.Equal("Wayfarer", resolver.Resolve(14u));
    }

    [InstalledDatFact]
    public void Resolve_UnmappedId_ReturnsNull()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var resolver = new CharacterTitleResolver(new DatCollectionAdapter(dats));

        Assert.Null(resolver.Resolve(0xFFFFFFFEu));
    }

    [InstalledDatFact]
    public void Resolve_CachesTheEnumMapperAcrossCalls()
    {
        using var dats = new DatCollection(DatDirectory, DatReaderWriter.Options.DatAccessType.Read);
        var resolver = new CharacterTitleResolver(new DatCollectionAdapter(dats));

        string? first = resolver.Resolve(13u);
        string? second = resolver.Resolve(13u);

        Assert.Equal("War Mage", first);
        Assert.Equal(first, second);
    }
}
