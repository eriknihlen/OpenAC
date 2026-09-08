using AcDream.Content.CharGen;
using AcDream.Core.CharGen;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit.Abstractions;

namespace AcDream.Content.Tests.CharGen;

[Trait("Lane", "InstalledDat")]
public sealed class ChargenAppearanceCatalogColorTests
{
    private readonly ITestOutputHelper _out;
    public ChargenAppearanceCatalogColorTests(ITestOutputHelper output) => _out = output;

    private const uint AluvianId = 1u;
    private const int MaleGenderKey = 1;

    private static string? ResolveDatDir()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;
        string def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    private static (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog)? LoadAluvianMale(
        DatCollectionAdapter adapter)
    {
        ChargenOptions options = ChargenTableReader.Load(adapter);
        if (!options.TryGetHeritage(AluvianId, out ChargenHeritageOptions? heritage))
            return null;
        if (!heritage.GendersByKey.TryGetValue(MaleGenderKey, out ChargenGenderOptions? gender))
            return null;
        return (gender, new ChargenAppearanceCatalog(adapter));
    }

    [Fact]
    public void TryGetColor_EyePaletteAtFixedIndex_MatchesMeasuredPixel()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var loaded = LoadAluvianMale(adapter);
        Assert.NotNull(loaded);
        (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog) = loaded!.Value;
        Assert.NotEmpty(gender.EyeColors);

        bool ok = catalog.TryGetColor(
            gender.EyeColors[0], ChargenSwatchColorResolver.EyeSampleIndex, out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(15, 63, 93), color);

        catalog.TryGetColor(gender.EyeColors[0], ChargenSwatchColorResolver.EyeSampleIndex, out ChargenSwatchRgb again);
        Assert.Equal(color, again);
    }

    [Fact]
    public void TryGetColor_OutOfRangeIndex_ReturnsFalse()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var loaded = LoadAluvianMale(adapter);
        Assert.NotNull(loaded);
        (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog) = loaded!.Value;

        bool ok = catalog.TryGetColor(gender.EyeColors[0], index: int.MaxValue, out _);

        Assert.False(ok);
    }

    [Fact]
    public void HairSwatchZero_AveragedAcrossPalSet_MatchesMeasuredColor()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var loaded = LoadAluvianMale(adapter);
        Assert.NotNull(loaded);
        (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog) = loaded!.Value;
        Assert.NotEmpty(gender.HairColors);

        bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            catalog, catalog, gender.HairColors[0], ChargenSwatchColorResolver.HairSampleIndex,
            out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(101, 94, 4), color);
    }

    [Fact]
    public void EveryHairColorSwatch_ResolvesToADistinctColor()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var loaded = LoadAluvianMale(adapter);
        Assert.NotNull(loaded);
        (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog) = loaded!.Value;
        Assert.True(gender.HairColors.Count > 1, "fixture assumption: Aluvian male has >1 hair color choice");

        var seen = new HashSet<ChargenSwatchRgb>();
        foreach (uint palSetId in gender.HairColors)
        {
            bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
                catalog, catalog, palSetId, ChargenSwatchColorResolver.HairSampleIndex, out ChargenSwatchRgb color);
            Assert.True(ok);
            _out.WriteLine($"hair palSet=0x{palSetId:X8} rgb=({color.R},{color.G},{color.B})");
            Assert.True(seen.Add(color), $"duplicate representative color {color} for palSet 0x{palSetId:X8}");
        }
    }

    [Fact]
    public void HeadgearSwatchZero_ResolvesThroughTheEquippedGarmentsClothingTable()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var loaded = LoadAluvianMale(adapter);
        Assert.NotNull(loaded);
        (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog) = loaded!.Value;
        Assert.NotEmpty(gender.Headgears);
        Assert.NotEmpty(gender.ClothingColors);

        ChargenGearOption firstHeadgear = gender.Headgears[0];
        bool palSetOk = ChargenSwatchColorResolver.TryGetClothingSwatchPalSetId(
            catalog, firstHeadgear.ClothingTableId, gender.ClothingColors[0], out uint palSetId);
        Assert.True(palSetOk);
        Assert.Equal(0x0F000009u, palSetId);

        bool colorOk = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            catalog, catalog, palSetId, ChargenSwatchColorResolver.ClothingSampleIndex, out ChargenSwatchRgb color);
        Assert.True(colorOk);
        Assert.Equal(new ChargenSwatchRgb(59, 59, 59), color);
    }

    [Fact]
    public void SkinFamilySwatch_ResolvesFromTheSharedSkinPalSet()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        var loaded = LoadAluvianMale(adapter);
        Assert.NotNull(loaded);
        (ChargenGenderOptions gender, ChargenAppearanceCatalog catalog) = loaded!.Value;

        bool ok = ChargenSwatchColorResolver.TryGetPalSetAverageColor(
            catalog, catalog, gender.SkinPalSetId, ChargenSwatchColorResolver.SkinFamilySampleIndex,
            out ChargenSwatchRgb color);

        Assert.True(ok);
        Assert.Equal(new ChargenSwatchRgb(182, 148, 118), color); // a plausible flesh tone.
    }
}
