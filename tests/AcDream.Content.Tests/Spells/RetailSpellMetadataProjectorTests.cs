using AcDream.Content;
using AcDream.Core.Spells;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using DatMagicSchool = DatReaderWriter.Enums.MagicSchool;
using DatSpellCategory = DatReaderWriter.Enums.SpellCategory;
using CoreSpellTable = AcDream.Core.Spells.SpellTable;

namespace AcDream.Content.Tests.Spells;

public sealed class RetailSpellMetadataProjectorTests
{
    [Fact]
    public void Project_PreservesRetailFieldsAndDerivesPresentationMetadata()
    {
        var components = new SpellComponentTable();
        components.Components.Add(10u, Component(ComponentType.Herb, "Malar"));
        components.Components.Add(11u, Component(ComponentType.Powder, "Caza"));
        components.Components.Add(12u, Component(ComponentType.Potion, "El"));

        var source = new SpellBase
        {
            Name = "Incantation of Test",
            Description = "A projected retail spell.",
            Components = [0xC1u, 10u, 11u, 12u, 63u, 0x31u],
            School = DatMagicSchool.WarMagic,
            Icon = 0x06001234u,
            Category = (DatSpellCategory)77u,
            Bitfield = SpellIndex.Resistable | SpellIndex.Projectile | SpellIndex.FastCast,
            BaseMana = 50u,
            BaseRangeConstant = 40f,
            BaseRangeMod = 0.25f,
            Power = 400u,
            SpellEconomyMod = -1f,
            FormulaVersion = 3u,
            ComponentLoss = 0.2f,
            MetaSpellType = SpellType.Projectile,
            MetaSpellId = 5000u,
            CasterEffect = (PlayScript)6u,
            TargetEffect = (PlayScript)7u,
            FizzleEffect = (PlayScript)8u,
            RecoveryInterval = 1.5,
            RecoveryAmount = 2.5f,
            DisplayOrder = 1234u,
            NonComponentTargetType = ItemType.Creature,
            ManaMod = 14u,
        };

        SpellMetadata actual = RetailSpellMetadataProjector.Project(
            5000u, source, components);

        Assert.Equal("Incantation of Test", actual.Name);
        Assert.Equal("A projected retail spell.", actual.Description);
        Assert.Equal("War Magic", actual.School);
        Assert.Equal(AcDream.Core.Spells.MagicSchool.WarMagic, actual.SchoolId);
        Assert.Equal(77u, actual.Family);
        Assert.Equal(0x06001234u, actual.IconId);
        Assert.Equal("Malar Cazael", actual.SpellWords);
        Assert.Equal(8, actual.Generation);
        Assert.Equal(0x10u, actual.TargetMask);
        Assert.False(actual.IsUntargeted);
        Assert.True(actual.IsProjectile);
        Assert.True(actual.IsFastWindup);
        Assert.True(actual.IsOffensive);
        Assert.Equal(source.Components, actual.FormulaComponents);
        Assert.Equal(3u, actual.FormulaVersion);
        Assert.Equal(14u, actual.ManaModifier);
        Assert.Equal((uint)ItemType.Creature, actual.NonComponentTargetType);
        Assert.Equal(0x10u, actual.FormulaTargetType);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Load_EndOfRetailDat_ResolvesEveryLateLearnedSpellMissingFromCsv()
    {
        string? datDir = ResolveDatDir();
        string? csvPath = ResolveRepoFile("docs", "research", "data", "spells.csv");
        if (datDir is null || csvPath is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var rawDats = new DatCollection(datDir, DatAccessType.Read);
        using var dats = new DatCollectionAdapter(rawDats);
        MagicCatalog catalog = MagicCatalog.Load(dats);
        CoreSpellTable legacy = CoreSpellTable.LoadFromCsv(csvPath);

        Assert.Equal(6266, catalog.SpellTable.Count);
        Assert.Equal(3956, legacy.Count);
        uint[] lateSpellIds = catalog.SpellTable.SpellIds
            .Where(id => !legacy.TryGet(id, out _))
            .ToArray();
        Assert.Equal(2310, lateSpellIds.Length);

        var spellbook = new Spellbook(catalog.SpellTable);
        foreach (uint spellId in lateSpellIds)
        {
            spellbook.OnSpellLearned(spellId);
            Assert.True(spellbook.TryGetMetadata(spellId, out SpellMetadata metadata));
            Assert.Equal(spellId, metadata.SpellId);
        }
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Load_EndOfRetailDat_UsesRetailFormulaTargetsInsteadOfLegacyExportMasks()
    {
        string? datDir = ResolveDatDir();
        string? csvPath = ResolveRepoFile("docs", "research", "data", "spells.csv");
        if (datDir is null || csvPath is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var rawDats = new DatCollection(datDir, DatAccessType.Read);
        using var dats = new DatCollectionAdapter(rawDats);
        MagicCatalog catalog = MagicCatalog.Load(dats);
        CoreSpellTable legacy = CoreSpellTable.LoadFromCsv(csvPath);
        var mismatches = new List<string>();
        foreach (uint spellId in legacy.SpellIds)
        {
            if (!legacy.TryGet(spellId, out SpellMetadata oldSpell)
                || !catalog.SpellTable.TryGet(spellId, out SpellMetadata datSpell))
                continue;
            if (oldSpell.TargetMask != datSpell.TargetMask)
                mismatches.Add(
                    $"{spellId}:{oldSpell.Name} csv=0x{oldSpell.TargetMask:X8} " +
                    $"dat=0x{datSpell.TargetMask:X8} formulaTarget=0x{datSpell.FormulaTargetType:X8} " +
                    $"formula=[{string.Join(',', datSpell.FormulaComponents)}]");
        }

        Assert.Equal(378, mismatches.Count);
        Assert.Contains(mismatches, value => value.StartsWith("35:Blood Drinker I "));
        Assert.Contains(mismatches, value => value.Contains("csv=0x00000101 dat=0x00000010"));
    }

    [Fact]
    public void Project_ComponentSet_TakesHerbPowderPotionTalismanAndIgnoresScarabsAndTapers()
    {
        var components = new SpellComponentTable();
        components.Components.Add(1u, Component(ComponentType.Scarab, string.Empty));
        components.Components.Add(6u, Component(ComponentType.Scarab, string.Empty));
        components.Components.Add(7u, Component(ComponentType.Herb, "Malar"));
        components.Components.Add(33u, Component(ComponentType.Powder, "Caza"));
        components.Components.Add(44u, Component(ComponentType.Potion, "El"));
        components.Components.Add(49u, Component(ComponentType.Talisman, string.Empty));
        components.Components.Add(60u, Component(ComponentType.Talisman, string.Empty));
        components.Components.Add(63u, Component(ComponentType.Taper, string.Empty));
        components.Components.Add(72u, Component(ComponentType.Taper, string.Empty));

        // "Strength Self I" shape: lead scarab, no tapers, Rowan talisman.
        SpellMetadata weakSelf = RetailSpellMetadataProjector.Project(
            2u, Formula([1u, 7u, 33u, 44u, 60u]), components);
        // "Strength Self VI" shape: pyreal scarab, two tapers, same talisman.
        SpellMetadata strongSelf = RetailSpellMetadataProjector.Project(
            0x0534u, Formula([6u, 63u, 7u, 72u, 33u, 44u, 63u, 60u]), components);
        // "Strength Other VI" shape: the SAME herb/powder/potion, Poplar.
        SpellMetadata strongOther = RetailSpellMetadataProjector.Project(
            0x0539u, Formula([6u, 63u, 7u, 72u, 33u, 44u, 63u, 49u]), components);

        Assert.Equal(new SpellComponentSet(7u, 33u, 44u, 60u), weakSelf.ComponentSet);
        Assert.Equal(weakSelf.ComponentSet, strongSelf.ComponentSet);
        Assert.NotEqual(weakSelf.ComponentSet, strongOther.ComponentSet);
        Assert.Equal(new SpellComponentSet(7u, 33u, 44u, 49u), strongOther.ComponentSet);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void Load_EndOfRetailDat_ComponentSetSeparatesSelfFromOtherAcrossEveryTier()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var rawDats = new DatCollection(datDir, DatAccessType.Read);
        using var dats = new DatCollectionAdapter(rawDats);
        MagicCatalog catalog = MagicCatalog.Load(dats);

        SpellComponentSet Set(uint spellId)
        {
            Assert.True(catalog.SpellTable.TryGet(spellId, out SpellMetadata meta));
            return meta.ComponentSet;
        }

        var strengthSelf = new SpellComponentSet(7u, 33u, 44u, 60u);
        Assert.Equal(strengthSelf, Set(0x0002u));   // Strength Self I
        Assert.Equal(strengthSelf, Set(0x0534u));   // Strength Self VI
        Assert.Equal(strengthSelf, Set(0x10E5u));   // Incantation of Strength Self
        // Strength Other VI and Incantation of Strength Other: Poplar 49.
        Assert.Equal(new SpellComponentSet(7u, 33u, 44u, 49u), Set(0x0539u));
        Assert.Equal(new SpellComponentSet(7u, 33u, 44u, 49u), Set(0x10E4u));

        var masterySelf = new SpellComponentSet(7u, 25u, 42u, 60u);
        Assert.Equal(masterySelf, Set(0x022Du));    // CE Mastery Self I
        Assert.Equal(masterySelf, Set(0x0232u));    // CE Mastery Self VI
        Assert.Equal(masterySelf, Set(0x11B2u));    // Incantation of CE Mastery Self
        Assert.Equal(masterySelf, Set(0x08A7u));
        Assert.NotEqual(masterySelf, Set(0x08A6u)); // Adja's Boon is NOT
        Assert.Equal(new SpellComponentSet(7u, 25u, 42u, 49u), Set(0x08A6u));
    }

    private static SpellBase Formula(List<uint> components) => new()
    {
        Name = "Fixture",
        Description = "Fixture",
        Components = components,
        School = DatMagicSchool.CreatureEnchantment,
        Bitfield = SpellIndex.SelfTargeted,
        Category = (DatSpellCategory)1u,
    };

    private static SpellComponentBase Component(ComponentType type, string text) => new()
    {
        Type = type,
        Text = text,
    };

    private static string? ResolveDatDir()
    {
        string? fromEnv = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
            return fromEnv;
        string fallback = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static string? ResolveRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}
