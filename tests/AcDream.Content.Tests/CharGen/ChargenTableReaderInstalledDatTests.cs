using AcDream.Content.CharGen;
using AcDream.Core.CharGen;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.Content.Tests.CharGen;

[Trait("Lane", "InstalledDat")]
public sealed class ChargenTableReaderInstalledDatTests
{
    private const uint AluvianId = 1u;
    private const uint GharundimId = 2u;
    private const uint ShoId = 3u;
    private const uint ViamontianId = 4u;
    private const uint OlthoiId = 12u;
    private const uint OlthoiAcidId = 13u;

    [Fact]
    public void InstalledCharGenTable_LoadsAndHasThirteenHeritageGroups()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.Equal(13, options.HeritagesById.Count);
        Assert.NotEmpty(options.StarterAreas);
    }

    [Fact]
    public void InstalledCharGenTable_HasTheFourNamedHeritagesWithRetailDisplayNames()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.True(options.TryGetHeritage(AluvianId, out ChargenHeritageOptions? aluvian));
        Assert.Equal("Aluvian", aluvian!.Name);

        Assert.True(options.TryGetHeritage(GharundimId, out ChargenHeritageOptions? gharundim));
        Assert.Equal("Gharu'ndim", gharundim!.Name);

        Assert.True(options.TryGetHeritage(ShoId, out ChargenHeritageOptions? sho));
        Assert.Equal("Sho", sho!.Name);

        Assert.True(options.TryGetHeritage(ViamontianId, out ChargenHeritageOptions? viamontian));
        Assert.Equal("Viamontian", viamontian!.Name);

        Assert.True(options.TryGetHeritage(OlthoiId, out ChargenHeritageOptions? olthoi));
        Assert.True(olthoi!.IsOlthoi);
        Assert.True(options.TryGetHeritage(OlthoiAcidId, out ChargenHeritageOptions? olthoiAcid));
        Assert.True(olthoiAcid!.IsOlthoi);
    }

    [Fact]
    public void InstalledHeritages_EachHasAtLeastOneGenderWithNonEmptyAppearanceOptions()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.NotEmpty(options.HeritagesById);
        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            Assert.NotEmpty(heritage.GendersByKey);
            Assert.Contains(
                heritage.GendersByKey.Values,
                gender => gender.HasAnyAppearanceOptions);
        }
    }

    [Fact]
    public void InstalledHeritages_EveryTemplateAttributeSpreadFitsTheAttributeBudget()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        int templatesChecked = 0;
        bool anyFullySpentAnywhere = false;
        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            foreach (ChargenTemplate template in heritage.Templates)
            {
                templatesChecked++;
                Assert.True(
                    ChargenAttributeMath.AreAllWithinRange(template.Attributes),
                    $"{heritage.Name}/{template.Name}: an attribute fell outside " +
                    $"{ChargenAttributeMath.AttributeMin}..{ChargenAttributeMath.AttributeMax} " +
                    $"({template.Attributes}).");
                Assert.True(
                    template.Attributes.Total <= heritage.AttributeCredits,
                    $"{heritage.Name}/{template.Name}: attribute spread totals " +
                    $"{template.Attributes.Total}, exceeding the {heritage.AttributeCredits}-credit budget.");
                anyFullySpentAnywhere |= ChargenAttributeMath.IsFullySpent(heritage.AttributeCredits, template.Attributes);
            }
        }

        Assert.True(templatesChecked > 0, "Expected at least one profession template across all heritages.");
        Assert.True(
            anyFullySpentAnywhere,
            "Expected at least one named preset template to fully spend its heritage's credit budget.");
    }

    [Fact]
    public void InstalledHeritages_PrimaryAndSecondaryStartAreaIndicesResolveIntoTheSharedList()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        int indicesChecked = 0;
        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            foreach (int index in heritage.PrimaryStartAreaIndices.Concat(heritage.SecondaryStartAreaIndices))
            {
                indicesChecked++;
                Assert.True(
                    options.TryGetStarterArea(index, out ChargenStarterArea? area),
                    $"{heritage.Name}: start-area index {index} does not resolve into the shared StarterAreas list.");
                Assert.False(string.IsNullOrEmpty(area!.Name));
            }
        }

        Assert.True(indicesChecked > 0, "Expected at least one heritage start-area reference.");
    }

    [Fact]
    public void InstalledHeritages_SkillCostsResolveToKnownWireSkillIds()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.True(options.TryGetHeritage(AluvianId, out ChargenHeritageOptions? aluvian));
        Assert.NotEmpty(aluvian!.SkillCostsBySkillId);
        foreach (var pair in aluvian.SkillCostsBySkillId)
        {
            Assert.Equal(pair.Key, pair.Value.SkillId);
            Assert.InRange(pair.Key, 1u, 54u);
            Assert.True(pair.Value.NormalCost >= 0);
            Assert.True(pair.Value.PrimaryCost >= 0);
        }
    }

    [Fact]
    public void InstalledHeritages_SkillCostFallbackCoversTheKnownUncostableSkillSet()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.Equal(38, options.GlobalSkillCostsBySkillId.Count);

        uint[] expectedUncostableSkillIds =
            [1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 17, 25, 26, 42, 53];

        Assert.NotEmpty(options.HeritagesById);
        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            Assert.Single(heritage.SkillCostsBySkillId);

            var uncostable = new List<uint>();
            foreach (KeyValuePair<uint, ChargenSkillCost> pair in heritage.SkillCostsBySkillId)
            {
                Assert.True(
                    options.GlobalSkillCostsBySkillId.ContainsKey(pair.Key),
                    $"{heritage.Name}: skill {pair.Key} is heritage-only with no global fallback entry — " +
                    "new ground truth found; update this test's recorded reality.");
            }

            for (uint skillId = 1; skillId < ChargenSkillAdvancementSet.SlotCount; skillId++)
            {
                bool inHeritage = heritage.SkillCostsBySkillId.ContainsKey(skillId);
                bool inGlobal = options.GlobalSkillCostsBySkillId.ContainsKey(skillId);
                if (!inHeritage && !inGlobal)
                    uncostable.Add(skillId);
            }

            Assert.Equal(expectedUncostableSkillIds, uncostable.OrderBy(id => id));
        }
    }

    [Fact]
    public void InstalledSkillTable_GlobalSkillDetails_MinLevelDistributionMatchesCostCoverage()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.Equal(options.GlobalSkillCostsBySkillId.Count, options.GlobalSkillDetailsBySkillId!.Count);
        Assert.Equal(38, options.GlobalSkillDetailsBySkillId.Count);

        int useableUntrained = 0;
        int trainedRequired = 0;
        bool anyDescriptionNonEmpty = false;
        foreach (ChargenSkillDetail detail in options.GlobalSkillDetailsBySkillId.Values)
        {
            Assert.True(detail.MinLevel <= 2u, $"skill {detail.SkillId}: MinLevel {detail.MinLevel} exceeds Trained(2).");
            if (detail.MinLevel <= 1u)
                useableUntrained++;
            else
                trainedRequired++;
            anyDescriptionNonEmpty |= !string.IsNullOrEmpty(detail.Description);
        }

        Assert.Equal(23, useableUntrained);
        Assert.Equal(15, trainedRequired);
        Assert.True(anyDescriptionNonEmpty, "Expected at least one skill to carry a non-empty description.");
    }

    [Fact]
    public void InstalledHeritages_AppearanceOptionListsRecordedPerListCompleteness()
    {
        string? datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null)
        {
            Console.WriteLine("SKIP: installed retail DAT directory is unavailable.");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        ChargenOptions options = ChargenTableReader.Load(adapter);

        Assert.NotEmpty(options.HeritagesById);
        var emptyLists = new List<string>();
        foreach (ChargenHeritageOptions heritage in options.HeritagesById.Values)
        {
            foreach (KeyValuePair<int, ChargenGenderOptions> genderPair in heritage.GendersByKey)
            {
                ChargenGenderOptions gender = genderPair.Value;
                string label = $"{heritage.Name}/{gender.Name} (heritage={heritage.HeritageId}, gender={genderPair.Key})";

                void RecordIfEmpty(string listName, int count)
                {
                    if (count == 0)
                        emptyLists.Add($"{label}: {listName}");
                }

                RecordIfEmpty(nameof(gender.HairStyles), gender.HairStyles.Count);
                RecordIfEmpty(nameof(gender.EyeStrips), gender.EyeStrips.Count);
                RecordIfEmpty(nameof(gender.NoseStrips), gender.NoseStrips.Count);
                RecordIfEmpty(nameof(gender.MouthStrips), gender.MouthStrips.Count);
                RecordIfEmpty(nameof(gender.Headgears), gender.Headgears.Count);
                RecordIfEmpty(nameof(gender.Shirts), gender.Shirts.Count);
                RecordIfEmpty(nameof(gender.Pants), gender.Pants.Count);
                RecordIfEmpty(nameof(gender.Footwear), gender.Footwear.Count);
                RecordIfEmpty(nameof(gender.HairColors), gender.HairColors.Count);
                RecordIfEmpty(nameof(gender.EyeColors), gender.EyeColors.Count);
                RecordIfEmpty(nameof(gender.ClothingColors), gender.ClothingColors.Count);
            }
        }

        Assert.Empty(emptyLists);
    }
}
