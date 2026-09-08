using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class AttackSpellCatalogTests
{
    [Theory]
    [InlineData("Force Bolt VII", "piercing damage", 2)]
    [InlineData("Shock Wave VII", "bludgeoning damage", 3)]
    [InlineData("Whirling Blade VII", "slashing damage", 1)]
    [InlineData("Nether Bolt VII", "nether damage", 8)]
    public void RetailDescriptionDeterminesDamageElement(
        string name,
        string description,
        int expected)
    {
        PluginSpellInfo spell = Spell(1, name, description) with
        {
            TargetMask = 0x10,
            IsProjectile = true,
        };

        Assert.True(AttackSpellCatalog.TryClassify(spell, out var choice));
        Assert.Equal((MonsterDamageType)expected, choice.DamageType);
        Assert.Equal(AttackSpellShape.Direct, choice.Shape);
    }

    [Theory]
    [InlineData("Incantation of Piercing Lure")]
    [InlineData("Piercing Vulnerability Other VII")]
    public void VulnerabilityDebuffsAreNeverClassifiedAsDamageSpells(string name)
    {
        PluginSpellInfo spell = Spell(
            1,
            name,
            "Makes the target more vulnerable to piercing damage.") with
        {
            TargetMask = 0x10,
            IsDebuff = true,
        };

        Assert.False(AttackSpellCatalog.TryClassify(spell, out _));
    }

    [Fact]
    public void ArcIsPreferredOnlyAtOrBeyondArcRange()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
            Spell(2, "Flame Arc VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);
        var settings = new CombatSettings { UseArcs = UseArcsMode.AtRange, ArcRange = 15 };
        var actions = new MonsterRuleActions
        {
            DamageType = MonsterDamageType.Fire,
        };

        Assert.Equal(AttackSpellShape.Direct, catalog.Candidates(
            actions, settings, Target(5), 0, Character.Instance)[0].Shape);
        Assert.Equal(AttackSpellShape.Arc, catalog.Candidates(
            actions, settings, Target(20), 0, Character.Instance)[0].Shape);
    }

    [Fact]
    public void StreakFlagPrefersStreakAndRetainsDirectFallback()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
            Spell(2, "Flame Streak VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);
        var actions = new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack | MonsterActionFlags.Streak,
            DamageType = MonsterDamageType.Fire,
        };

        IReadOnlyList<AttackSpellChoice> choices = catalog.Candidates(
            actions, new CombatSettings(), Target(5), 0, Character.Instance);

        Assert.Equal(AttackSpellShape.Streak, choices[0].Shape);
        Assert.Contains(choices, choice => choice.Shape == AttackSpellShape.Direct);
    }

    [Fact]
    public void AttackPlusRingRequiresThresholdButRingOnlyRequiresOne()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
            Spell(2, "Flame Ring", "fire damage outward from the caster") with
            {
                TargetMask = 0,
            },
        ]);
        var settings = new CombatSettings { MinimumRingTargets = 4 };
        var attackAndRing = new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Attack | MonsterActionFlags.Ring,
            DamageType = MonsterDamageType.Fire,
        };
        var ringOnly = attackAndRing with { Flags = MonsterActionFlags.Ring };

        Assert.Equal(AttackSpellShape.Direct, catalog.Candidates(
            attackAndRing, settings, Target(3), 3, Character.Instance)[0].Shape);
        Assert.Equal(AttackSpellShape.Ring, catalog.Candidates(
            attackAndRing, settings, Target(3), 4, Character.Instance)[0].Shape);
        Assert.Equal(AttackSpellShape.Ring, catalog.Candidates(
            ringOnly, settings, Target(3), 1, Character.Instance)[0].Shape);
    }

    [Fact]
    public void HarmAndVoidModesDoNotCrossSelectSpellFamilies()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Harm Other VII", "Drains the target's Health."),
            Spell(2, "Nether Bolt VII", "nether damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);

        Assert.All(catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.Harm },
            new CombatSettings(), Target(3), 0, Character.Instance),
            choice => Assert.Equal(AttackSpellShape.Harm, choice.Shape));
        Assert.All(catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.VoidBasic },
            new CombatSettings(), Target(3), 0, Character.Instance),
            choice => Assert.Equal(MonsterDamageType.Nether, choice.DamageType));
    }

    [Fact]
    public void AutoUsesVoidWhenWarIsUntrainedAndVoidIsTrained()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
            Spell(2, "Nether Bolt VII", "nether damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);
        var character = new Character(
        [
            new PluginSkillInfo(
                43,
                "Void Magic",
                PluginSkillTraining.Trained,
                400),
        ]);

        IReadOnlyList<AttackSpellChoice> choices = catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.Auto },
            new CombatSettings(),
            Target(3),
            0,
            character);

        Assert.NotEmpty(choices);
        Assert.All(choices, choice =>
            Assert.Equal(MonsterDamageType.Nether, choice.DamageType));
    }

    [Fact]
    public void AutoUsesOfficialMonsterOverrideBeforeSpellShapeOrTier()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
            Spell(2, "Frost Arc VI", "cold damage") with
            {
                Tier = 6,
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);
        PluginCombatTarget target = Target(5) with
        {
            Name = "Magma Golem",
            SpeciesId = 1,
        };

        IReadOnlyList<AttackSpellChoice> choices = catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.Auto },
            new CombatSettings { UseArcs = UseArcsMode.No },
            target,
            0,
            Character.Instance);

        Assert.Equal(MonsterDamageType.Cold, choices[0].DamageType);
        Assert.Equal(AttackSpellShape.Arc, choices[0].Shape);
    }

    [Fact]
    public void PrismaticRetainsAutomaticMagicElementSelection()
    {
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(1, "Flame Bolt VII", "fire damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
            Spell(2, "Frost Bolt VII", "cold damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);
        PluginCombatTarget target = Target(5) with
        {
            Name = "Magma Golem",
            SpeciesId = 1,
        };

        IReadOnlyList<AttackSpellChoice> choices = catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.Prismatic },
            new CombatSettings(),
            target,
            0,
            Character.Instance);

        Assert.NotEmpty(choices);
        Assert.Equal(MonsterDamageType.Cold, choices[0].DamageType);
    }

    [Fact]
    public void FistsUsesTuskerSpellOnlyWhileTuskerFistsEnchantmentIsActive()
    {
        const uint tuskerFists = 0x0B76u;
        var catalog = AttackSpellCatalog.Build(
        [
            Spell(tuskerFists, "Tusker Fists", string.Empty) with
            {
                TargetMask = 0x10,
            },
            Spell(2, "Shock Wave VII", "bludgeoning damage") with
            {
                TargetMask = 0x10,
                IsProjectile = true,
            },
        ]);
        var active = new Character(
            enchantments:
            [
                new PluginActiveEnchantment(
                    tuskerFists,
                    Family: tuskerFists,
                    Tier: 1,
                    SecondsRemaining: 60),
            ]);

        Assert.Equal(tuskerFists, catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.Fists },
            new CombatSettings(), Target(3), 0, active)[0].Spell.SpellId);
        Assert.Equal(MonsterDamageType.Bludgeon, catalog.Candidates(
            new MonsterRuleActions { DamageType = MonsterDamageType.Fists },
            new CombatSettings(), Target(3), 0, Character.Instance)[0].DamageType);
    }

    private static PluginSpellInfo Spell(
        uint id,
        string name,
        string description) => new(
            id,
            name,
            Family: id,
            Tier: 7,
            Difficulty: 300,
            ManaCost: 35,
            DurationSeconds: 0,
            School: 34,
            Description: description,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            IsOffensive = true,
        };

    private static PluginCombatTarget Target(float distance) => new(
        10, "Target", 100, distance, 0, true, 1f);

    private sealed class Character(
        IReadOnlyList<PluginSkillInfo>? skills = null,
        IReadOnlyList<PluginActiveEnchantment>? enchantments = null) : ICharacterInfo
    {
        public static Character Instance { get; } = new();
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
