using AcDream.Content.CharGen;
using AcDream.Core.CharGen;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit.Abstractions;

namespace AcDream.Content.Tests.CharGen;

[Trait("Lane", "InstalledDat")]
public sealed class ChargenAppearanceCatalogInstalledDatTests
{
    private readonly ITestOutputHelper _out;
    public ChargenAppearanceCatalogInstalledDatTests(ITestOutputHelper output) => _out = output;

    private const uint TumerokId = 7u;
    private const uint UndeadId = 11u;

    private static readonly uint[] StandardZeroGapHeritageIds = [1u, 2u, 3u, 4u, 5u, TumerokId, 8u, 9u, 10u];

    private static readonly uint[] UndeadMeasuredMissingClothingTableIds =
        [0x10000009u, 0x100000F9u, 0x10000001u, 0x10000007u];

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

    [Fact]
    public void EveryHeritageGendersDefaultSelection_ResolvesWithNoMissingDatIds()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.NotEmpty(options.HeritagesById);
        var catalog = new ChargenAppearanceCatalog(adapter);

        int composed = 0;
        var missingSummaries = new List<string>();
        var baseEffectGapFailures = new List<string>();

        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            foreach ((int genderKey, ChargenGenderOptions gender) in heritage.GendersByKey)
            {
                ChargenAppearanceSelection selection = MakeDefaultSelection(gender);

                bool ok = ChargenAppearanceFactory.TryCompose(
                    options, heritage.HeritageId, genderKey, selection,
                    catalog, catalog, out ChargenAppearanceResult result);

                Assert.True(ok, $"heritage=0x{heritage.HeritageId:X} gender={genderKey} failed to resolve heritage/gender");
                composed++;

                if (result.MissingPalSetIds.Count > 0 || result.MissingClothingTableIds.Count > 0)
                {
                    missingSummaries.Add(
                        $"heritage={heritage.Name} gender={genderKey}: "
                        + $"missingPalSets=[{string.Join(",", result.MissingPalSetIds.Select(id => $"0x{id:X8}"))}] "
                        + $"missingClothingTables=[{string.Join(",", result.MissingClothingTableIds.Select(id => $"0x{id:X8}"))}]");
                }

                _out.WriteLine(
                    $"heritage={heritage.Name} (0x{heritage.HeritageId:X}) gender={genderKey} setup=0x{result.SetupId:X8}: "
                    + $"{result.ClothingTablesMissingBaseEffectForSetup.Count} clothing table(s) with no "
                    + "ClothingBaseEffects entry for this body setup "
                    + $"[{string.Join(",", result.ClothingTablesMissingBaseEffectForSetup.Select(id => $"0x{id:X8}"))}]");

                if (StandardZeroGapHeritageIds.Contains(heritage.HeritageId))
                {
                    if (result.ClothingTablesMissingBaseEffectForSetup.Count != 0)
                    {
                        baseEffectGapFailures.Add(
                            $"heritage={heritage.Name} gender={genderKey}: expected ZERO ClothingBaseEffects "
                            + $"gaps (a standard heritage with clothing UI shown), measured "
                            + $"{result.ClothingTablesMissingBaseEffectForSetup.Count}: "
                            + $"[{string.Join(",", result.ClothingTablesMissingBaseEffectForSetup.Select(id => $"0x{id:X8}"))}]");
                    }
                }
                else if (heritage.HeritageId == UndeadId)
                {
                    if (!result.ClothingTablesMissingBaseEffectForSetup.SequenceEqual(UndeadMeasuredMissingClothingTableIds))
                    {
                        baseEffectGapFailures.Add(
                            $"heritage=Undead gender={genderKey}: expected EXACTLY "
                            + $"[{string.Join(",", UndeadMeasuredMissingClothingTableIds.Select(id => $"0x{id:X8}"))}], measured "
                            + $"[{string.Join(",", result.ClothingTablesMissingBaseEffectForSetup.Select(id => $"0x{id:X8}"))}]");
                    }
                }
            }
        }

        _out.WriteLine($"composed {composed} heritage/gender selections.");
        Assert.True(
            missingSummaries.Count == 0,
            "Missing dat ids found:\n" + string.Join('\n', missingSummaries));
        Assert.True(
            baseEffectGapFailures.Count == 0,
            "TS-82 measurement drifted from its pinned expectation:\n" + string.Join('\n', baseEffectGapFailures));
        Assert.True(composed >= 13, $"Expected at least 13 heritage/gender combinations, composed {composed}.");
    }

    [Fact]
    public void EveryHairStyleOfEveryHeritageGender_ComposesToARealInstalledSetupId()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
        {
            _out.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);
        Assert.NotEmpty(options.HeritagesById);
        var catalog = new ChargenAppearanceCatalog(adapter);

        int sweptHairStyles = 0;
        var unresolvedSetups = new List<string>();

        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            foreach ((int genderKey, ChargenGenderOptions gender) in heritage.GendersByKey)
            {
                for (uint hairStyleIndex = 0; hairStyleIndex < (uint)gender.HairStyles.Count; hairStyleIndex++)
                {
                    ChargenAppearanceSelection selection = ChargenAppearanceSelection.Default with
                    {
                        HairStyle = hairStyleIndex,
                        SkinShade = 0.5,
                    };

                    bool ok = ChargenAppearanceFactory.TryCompose(
                        options, heritage.HeritageId, genderKey, selection,
                        catalog, catalog, out ChargenAppearanceResult result);
                    Assert.True(ok);
                    sweptHairStyles++;

                    if (adapter.Get<DatReaderWriter.DBObjs.Setup>(result.SetupId) is null)
                    {
                        unresolvedSetups.Add(
                            $"heritage={heritage.Name} gender={genderKey} hairStyle={hairStyleIndex}: "
                            + $"composed SetupId=0x{result.SetupId:X8} does not resolve to an installed Setup");
                    }
                }

                // Every gender is swept even with zero hair styles (still
                // exercises the "no hair style selected" default-setup path).
                if (gender.HairStyles.Count == 0)
                {
                    bool ok = ChargenAppearanceFactory.TryCompose(
                        options, heritage.HeritageId, genderKey,
                        ChargenAppearanceSelection.Default with { SkinShade = 0.5 },
                        catalog, catalog, out ChargenAppearanceResult result);
                    Assert.True(ok);
                    sweptHairStyles++;
                    if (adapter.Get<DatReaderWriter.DBObjs.Setup>(result.SetupId) is null)
                    {
                        unresolvedSetups.Add(
                            $"heritage={heritage.Name} gender={genderKey} (no hair styles): "
                            + $"composed SetupId=0x{result.SetupId:X8} does not resolve to an installed Setup");
                    }
                }
            }
        }

        _out.WriteLine($"swept {sweptHairStyles} hair-style/no-hair-style selections across 26 heritage/gender combinations.");
        Assert.True(
            unresolvedSetups.Count == 0,
            "Composed SetupId(s) that don't resolve to a real installed Setup:\n" + string.Join('\n', unresolvedSetups));
        Assert.True(sweptHairStyles > 26, $"Expected more than 26 swept selections (multiple hair styles per gender), got {sweptHairStyles}.");
    }

    private static ChargenAppearanceSelection MakeDefaultSelection(ChargenGenderOptions gender)
    {
        const double midShade = 0.5;
        ChargenAppearanceSelection selection = ChargenAppearanceSelection.Default;

        if (gender.HairStyles.Count > 0)
            selection = selection with { HairStyle = 0u };
        if (gender.EyeStrips.Count > 0)
            selection = selection with { EyesStrip = 0u };
        if (gender.NoseStrips.Count > 0)
            selection = selection with { NoseStrip = 0u };
        if (gender.MouthStrips.Count > 0)
            selection = selection with { MouthStrip = 0u };
        if (gender.HairColors.Count > 0)
            selection = selection with { HairColor = 0u, HairShade = midShade };
        if (gender.EyeColors.Count > 0)
            selection = selection with { EyeColor = 0u };

        if (gender.Headgears.Count > 0)
            selection = selection with { HeadgearStyle = 0u };
        if (gender.Shirts.Count > 0)
            selection = selection with { ShirtStyle = 0u };
        if (gender.Pants.Count > 0)
            selection = selection with { TrousersStyle = 0u };
        if (gender.Footwear.Count > 0)
            selection = selection with { FootwearStyle = 0u };

        if (gender.ClothingColors.Count > 0)
        {
            selection = selection with
            {
                HeadgearColor = 0u,
                HeadgearShade = midShade,
                ShirtColor = 0u,
                ShirtShade = midShade,
                TrousersColor = 0u,
                TrousersShade = midShade,
                FootwearColor = 0u,
                FootwearShade = midShade,
            };
        }

        return selection with { SkinShade = midShade };
    }
}
