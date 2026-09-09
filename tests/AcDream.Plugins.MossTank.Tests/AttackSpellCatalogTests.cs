using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class AttackSpellCatalogTests
{
    [Theory]
    [InlineData(MonsterDamageType.Acid, VtankCombatSpellType.War, "Acid Stream")]
    [InlineData(MonsterDamageType.Bludgeon, VtankCombatSpellType.War, "Shock Wave")]
    [InlineData(MonsterDamageType.Cold, VtankCombatSpellType.War, "Frost Bolt")]
    [InlineData(MonsterDamageType.Fire, VtankCombatSpellType.War, "Flame Bolt")]
    [InlineData(MonsterDamageType.Electric, VtankCombatSpellType.War, "Lightning Bolt")]
    [InlineData(MonsterDamageType.Pierce, VtankCombatSpellType.War, "Force Bolt")]
    [InlineData(MonsterDamageType.Slash, VtankCombatSpellType.War, "Whirling Blade")]
    [InlineData(MonsterDamageType.Harm, VtankCombatSpellType.War, "Martyr's Hecatomb")]
    [InlineData(MonsterDamageType.VoidBasic, VtankCombatSpellType.War, "Nether Bolt")]
    [InlineData(MonsterDamageType.Acid, VtankCombatSpellType.Arc, "Acid Arc")]
    [InlineData(MonsterDamageType.Bludgeon, VtankCombatSpellType.Arc, "Shock Arc")]
    [InlineData(MonsterDamageType.Cold, VtankCombatSpellType.Arc, "Frost Arc")]
    [InlineData(MonsterDamageType.Fire, VtankCombatSpellType.Arc, "Flame Arc")]
    [InlineData(MonsterDamageType.Electric, VtankCombatSpellType.Arc, "Lightning Arc")]
    [InlineData(MonsterDamageType.Pierce, VtankCombatSpellType.Arc, "Force Arc")]
    [InlineData(MonsterDamageType.Slash, VtankCombatSpellType.Arc, "Blade Arc")]
    [InlineData(MonsterDamageType.Harm, VtankCombatSpellType.Arc, "Harm Other")]
    [InlineData(MonsterDamageType.VoidBasic, VtankCombatSpellType.Arc, "Nether Arc")]
    [InlineData(MonsterDamageType.Acid, VtankCombatSpellType.Ring, "Searing Disc")]
    [InlineData(MonsterDamageType.Bludgeon, VtankCombatSpellType.Ring, "Tectonic Rifts")]
    [InlineData(MonsterDamageType.Cold, VtankCombatSpellType.Ring, "Halo of Frost")]
    [InlineData(MonsterDamageType.Fire, VtankCombatSpellType.Ring, "Cassius' Ring of Fire")]
    [InlineData(MonsterDamageType.Electric, VtankCombatSpellType.Ring, "Eye of the Storm")]
    [InlineData(MonsterDamageType.Pierce, VtankCombatSpellType.Ring, "Nuhmudira's Spines")]
    [InlineData(MonsterDamageType.Slash, VtankCombatSpellType.Ring, "Horizon's Blades")]
    [InlineData(MonsterDamageType.Harm, VtankCombatSpellType.Ring, "Curse of Raven Fury")]
    [InlineData(MonsterDamageType.Acid, VtankCombatSpellType.Streak, "Acid Streak")]
    [InlineData(MonsterDamageType.Bludgeon, VtankCombatSpellType.Streak, "Shock Wave Streak")]
    [InlineData(MonsterDamageType.Cold, VtankCombatSpellType.Streak, "Frost Streak")]
    [InlineData(MonsterDamageType.Fire, VtankCombatSpellType.Streak, "Flame Streak")]
    [InlineData(MonsterDamageType.Electric, VtankCombatSpellType.Streak, "Lightning Streak")]
    [InlineData(MonsterDamageType.Pierce, VtankCombatSpellType.Streak, "Force Streak")]
    [InlineData(MonsterDamageType.Slash, VtankCombatSpellType.Streak, "Whirling Blade Streak")]
    [InlineData(MonsterDamageType.Harm, VtankCombatSpellType.Streak, "Harm Other")]
    [InlineData(MonsterDamageType.VoidBasic, VtankCombatSpellType.Streak, "Nether Streak")]
    [InlineData(MonsterDamageType.Acid, VtankCombatSpellType.Vuln, "Acid Vulnerability Other")]
    [InlineData(MonsterDamageType.Slash, VtankCombatSpellType.Vuln, "Blade Vulnerability Other")]
    [InlineData(MonsterDamageType.Harm, VtankCombatSpellType.Vuln, "Drain Health Other")]
    [InlineData(MonsterDamageType.VoidBasic, VtankCombatSpellType.Vuln, "Destructive Curse")]
    internal void FamilyTableMatchesRetailVerbatim(
        MonsterDamageType element,
        VtankCombatSpellType type,
        string expected) =>
        Assert.Equal(expected, AttackSpellCatalog.FamilyName(element, type));

    [Theory]
    [InlineData(MonsterDamageType.Auto)]
    [InlineData(MonsterDamageType.None)]
    [InlineData(MonsterDamageType.Random)]
    [InlineData(MonsterDamageType.Fists)]
    [InlineData(MonsterDamageType.DrainAuto)]
    [InlineData(MonsterDamageType.Prismatic)]
    [InlineData(MonsterDamageType.Physical)]
    internal void ElementsRetailHasNoCaseForResolveToNothing(MonsterDamageType element)
    {
        foreach (VtankCombatSpellType type in Enum.GetValues<VtankCombatSpellType>())
            Assert.Null(AttackSpellCatalog.FamilyName(element, type));
    }

    [Fact]
    public void QualityWalkTakesTheHighestKnownMemberOfTheLine()
    {
        AttackSpellCatalog catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Frost Bolt III", difficulty: 100),
            Spell(2, "Frost Bolt VII", difficulty: 300),
            Spell(3, "Incantation of Frost Bolt VII", difficulty: 350),
            Spell(4, "Frost Bolt V", difficulty: 200),
        ]);

        PluginSpellInfo? resolved = catalog.Resolve(
            MonsterDamageType.Cold,
            VtankCombatSpellType.War);

        Assert.Equal(3u, resolved?.SpellId);
    }

    [Fact]
    public void WallsBlastsAndVolleysAreNeverCandidates()
    {
        AttackSpellCatalog catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Frost Wall VII", difficulty: 400),
            Spell(2, "Frost Blast VII", difficulty: 380),
            Spell(3, "Frost Volley VII", difficulty: 370),
            Spell(4, "Frost Bolt II", difficulty: 50),
        ]);

        PluginSpellInfo? resolved = catalog.Resolve(
            MonsterDamageType.Cold,
            VtankCombatSpellType.War);

        Assert.Equal(4u, resolved?.SpellId);
        Assert.Null(catalog.Resolve(
            MonsterDamageType.Cold,
            VtankCombatSpellType.Arc));
    }

    [Fact]
    public void VoidRingIsResolvedBySpellIdNotByName()
    {
        // fk.cs:560 — `result = this.m_b.f.c(5361);` is the only entry in the
        // whole table that is not a name lookup.
        AttackSpellCatalog catalog = AttackSpellCatalog.Build(
        [
            Spell(AttackSpellCatalog.VoidRingSpellId, "Coldeve's Fury", difficulty: 400),
        ]);

        Assert.Equal(
            AttackSpellCatalog.VoidRingSpellId,
            catalog.Resolve(MonsterDamageType.VoidBasic, VtankCombatSpellType.Ring)
                ?.SpellId);
        Assert.Equal(
            AttackSpellCatalog.VoidRingSpellId,
            catalog.Resolve(MonsterDamageType.Nether, VtankCombatSpellType.Ring)
                ?.SpellId);
    }

    [Fact]
    public void UsabilityPredicateSkipsToTheNextBestMemberOfTheLine()
    {
        AttackSpellCatalog catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", difficulty: 300),
            Spell(2, "Flame Bolt IV", difficulty: 150),
        ]);

        Assert.Equal(
            2u,
            catalog.Resolve(
                MonsterDamageType.Fire,
                VtankCombatSpellType.War,
                spell => spell.Difficulty <= 200)?.SpellId);
        Assert.Null(catalog.Resolve(
            MonsterDamageType.Fire,
            VtankCombatSpellType.War,
            static _ => false));
    }

    [Theory]
    [InlineData("Frost Bolt VII", "Frost Bolt")]
    [InlineData("Incantation of Frost Bolt VII", "Frost Bolt")]
    [InlineData("Cassius' Ring of Fire", "Cassius' Ring of Fire")]
    [InlineData("Halo of Frost", "Halo of Frost")]
    [InlineData("Shock Wave Streak III", "Shock Wave Streak")]
    [InlineData("Harm Other I", "Harm Other")]
    public void BaseNameStripsOnlyTheTierSuffixAndIncantationPrefix(
        string name,
        string expected) =>
        Assert.Equal(expected, AttackSpellCatalog.BaseName(name));

    [Fact]
    public void AutoUsesVoidWhenWarIsUntrainedAndVoidIsTrained()
    {
        Assert.Equal(
            MonsterDamageType.Auto,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Auto,
                new Character([Skill(34), Skill(43)])));
        Assert.Equal(
            MonsterDamageType.VoidBasic,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Auto,
                new Character([Skill(43)])));
        Assert.Equal(
            MonsterDamageType.DrainAuto,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Auto,
                new Character([Skill(33)])));
        Assert.Equal(
            MonsterDamageType.Auto,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Auto,
                new Character()));
    }

    [Fact]
    public void PrismaticIsAnAmmunitionPolicyAndLeavesMagicOnAuto() =>
        Assert.Equal(
            MonsterDamageType.Auto,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Prismatic,
                new Character([Skill(34)])));

    [Fact]
    public void AFistsRowPlansBludgeonWhateverTheEnchantmentSays()
    {
        Assert.Equal(
            MonsterDamageType.Bludgeon,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Fists,
                new Character()));
        Assert.Equal(
            MonsterDamageType.Bludgeon,
            AttackSpellCatalog.ResolveMagicDamageMode(
                MonsterDamageType.Fists,
                new Character(
                    enchantments: [new PluginActiveEnchantment(0x0B76u, 1, 1, 60)])));
    }

    private static PluginSkillInfo Skill(uint id) => new(
        id, $"Skill {id}", PluginSkillTraining.Trained, 300);

    private static PluginSpellInfo Spell(
        uint id,
        string name,
        int difficulty) => new(
            id,
            name,
            Family: id,
            Tier: 7,
            Difficulty: difficulty,
            ManaCost: 35,
            DurationSeconds: 0,
            School: 34,
            Description: string.Empty,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            IsOffensive = true,
            IsProjectile = true,
            TargetMask = 0x10,
        };

    private sealed class Character(
        IReadOnlyList<PluginSkillInfo>? skills = null,
        IReadOnlyList<PluginActiveEnchantment>? enchantments = null) : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public IReadOnlyList<PluginSkillInfo> Skills => skills ?? [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments =>
            enchantments ?? [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in Skills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }
    }
}
