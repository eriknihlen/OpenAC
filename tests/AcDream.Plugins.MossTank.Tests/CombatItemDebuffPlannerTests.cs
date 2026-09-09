using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class CombatItemDebuffPlannerTests
{
    [Fact]
    public void SpellLevelAndSkillMethodsUseOfficialComparisonOrder()
    {
        PluginSpellInfo learned = Spell(1, "Imperil Other VI", 300, 31);
        PluginSpellInfo lensSpell = Spell(2, "Imperil Other VII", 350, 31);
        var catalog = new Catalog([learned], [learned, lensSpell]);
        var character = new Character((31u, 500u));
        PluginInventoryItem lens = Item(100, "Imperil Lens", 0x8000, 2) with
        {
            ItemSpellcraft = 100,
        };
        var settings = new CombatSettings
        {
            DebuffSelectionMethod = DebuffSelectionMethod.SpellLevel,
        };
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        MonsterRuleActions actions = Imperil();

        CombatDebuffSource byLevel = Assert.Single(
            CombatItemDebuffPlanner.Candidates(
                actions,
                settings,
                character,
                catalog,
                [lens],
                static (_, _) => true).Take(1));
        Assert.Equal(CombatDebuffSourceKind.CasterItem, byLevel.Kind);

        settings.DebuffSelectionMethod = DebuffSelectionMethod.Skill;
        CombatDebuffSource bySkill = Assert.Single(
            CombatItemDebuffPlanner.Candidates(
                actions,
                settings,
                character,
                catalog,
                [lens],
                static (_, _) => true).Take(1));
        Assert.Equal(CombatDebuffSourceKind.LearnedSpell, bySkill.Kind);
    }

    [Fact]
    public void SourcesAnswersForOneKindOnlyAndEmitsRetailsDebuffChoiceLines()
    {
        PluginSpellInfo imperil = Spell(1, "Imperil Other VI", 300, 31);
        PluginSpellInfo yield = Spell(2, "Magic Yield Other VII", 350, 31);
        PluginSpellInfo lensSpell = Spell(3, "Imperil Other VII", 350, 31);
        var catalog = new Catalog([imperil, yield], [imperil, yield, lensSpell]);
        var character = new Character((31u, 500u));
        PluginInventoryItem lens = Item(100, "Imperil Lens", 0x8000, 3) with
        {
            ItemSpellcraft = 900,
        };
        var settings = new CombatSettings
        {
            DebuffSelectionMethod = DebuffSelectionMethod.SpellLevel,
        };
        settings.CombatItemObjectIds.Add(lens.ObjectId);
        var log = new List<string>();

        IReadOnlyList<CombatDebuffSource> sources = CombatItemDebuffPlanner.Sources(
            new DebuffIdentity(MonsterActionFlags.Imperil, MonsterDamageType.Auto),
            settings,
            character,
            catalog,
            [lens],
            log.Add);

        Assert.All(sources, source =>
            Assert.Equal(MonsterActionFlags.Imperil, source.Identity.Flag));
        Assert.Equal(2, sources.Count);
        Assert.Equal(CombatDebuffSourceKind.CasterItem, sources[0].Kind);

        Assert.Contains("Find debuff choice (Imperil Lens [100]): Begin", log);
        Assert.Contains(
            "Find debuff choice (Imperil Lens [100]): Item set to be used, quality 350.",
            log);
        Assert.Contains("Find debuff choice (Imperil Lens [100]): Item tests done.", log);
    }

    [Fact]
    public void SourcesLogsRetailsWrongObjectTypeStopForAProfiledNonSource()
    {
        PluginSpellInfo imperil = Spell(1, "Imperil Other VI", 300, 31);
        var catalog = new Catalog([imperil], [imperil]);
        PluginInventoryItem armour = Item(101, "Studded Leather", 0x2, 0);
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(armour.ObjectId);
        var log = new List<string>();

        CombatItemDebuffPlanner.Sources(
            new DebuffIdentity(MonsterActionFlags.Imperil, MonsterDamageType.Auto),
            settings,
            new Character((31u, 500u)),
            catalog,
            [armour],
            log.Add);

        Assert.Contains(
            "Find debuff choice (Studded Leather [101]): Stop, wrong object type",
            log);
    }

    [Fact]
    public void ItemAndGrenadeSourcesRequireTheirExactProfiles()
    {
        PluginSpellInfo imperil = Spell(1323, "Imperil Other I", 100, 31);
        var catalog = new Catalog([], [imperil]);
        var character = new Character((38u, 400u));
        PluginInventoryItem grenade = Item(
            200,
            "Iron Phial of Imperil",
            0x100,
            0) with { CombatUse = 0 };
        var settings = new CombatSettings();

        Assert.Empty(CombatItemDebuffPlanner.Candidates(
            Imperil(), settings, character, catalog, [grenade],
            static (_, _) => true));

        settings.ConsumableNames.Add("Iron Phial of Imperil");
        CombatDebuffSource source = Assert.Single(
            CombatItemDebuffPlanner.Candidates(
                Imperil(), settings, character, catalog, [grenade],
                static (_, _) => true));
        Assert.Equal(CombatDebuffSourceKind.Grenade, source.Kind);
        Assert.Equal(200u, source.ItemObjectId);
        Assert.Equal(100, source.SourceSkill);
    }

    [Fact]
    public void ProcWeaponUsesFirstQualifyingAppraisedNonWarSpell()
    {
        PluginSpellInfo war = Spell(10, "Flame Bolt VII", 400, 34);
        PluginSpellInfo imperil = Spell(11, "Imperil Other VII", 350, 31);
        var catalog = new Catalog([], [war, imperil]);
        PluginInventoryItem weapon = Item(300, "Debuff Sword", 1, 0) with
        {
            ItemSpellcraft = 360,
            AppraisedSpellIds = [10u, 11u],
        };
        var settings = new CombatSettings();
        settings.CombatItemObjectIds.Add(300u);

        CombatDebuffSource source = Assert.Single(
            CombatItemDebuffPlanner.Candidates(
                Imperil(), settings, new Character(), catalog, [weapon],
                static (_, _) => true));

        Assert.Equal(CombatDebuffSourceKind.ProcWeapon, source.Kind);
        Assert.Equal(11u, source.Spell.SpellId);
    }

    private static MonsterRuleActions Imperil() => new()
    {
        Flags = MonsterActionFlags.Imperil,
    };

    private static PluginSpellInfo Spell(
        uint id,
        string name,
        int quality,
        uint school) => new(
            id, name, 1, 7, quality, 10, 60, school, string.Empty,
            false, false)
        {
            IsDebuff = true,
            IsOffensive = true,
            TargetMask = 0x10,
        };

    private static PluginInventoryItem Item(
        uint id,
        string name,
        uint itemType,
        uint spellId) => new(
            id, 0, name, itemType, 1, 0, 0, 0, 0, 0, 0,
            1, 0, 0, spellId, 0, 0, 0, false, 0, 0, 0, 0, 0,
            0, 0, 0);

    private sealed class Catalog(
        IReadOnlyList<PluginSpellInfo> known,
        IReadOnlyList<PluginSpellInfo> all) : ISpellCatalog
    {
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public IReadOnlyList<PluginSpellInfo> KnownCombatSpells => known;
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo spell in all)
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            info = default;
            return false;
        }
    }

    private sealed class Character(params (uint Id, uint Current)[] skills)
        : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public IReadOnlyList<PluginSkillInfo> Skills => skills.Select(
            skill => new PluginSkillInfo(
                skill.Id,
                string.Empty,
                PluginSkillTraining.Trained,
                skill.Current)).ToArray();
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach ((uint id, uint current) in skills)
            {
                if (id == skillId)
                {
                    skill = new PluginSkillInfo(
                        id,
                        string.Empty,
                        PluginSkillTraining.Trained,
                        current);
                    return true;
                }
            }
            skill = default;
            return false;
        }
    }
}
