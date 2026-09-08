using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class DebuffSchedulerTests
{
    [Theory]
    [InlineData("Fester Other VII", 1 << 0, 0)]
    [InlineData("Broadside of a Barn", 1 << 1, 0)]
    [InlineData("Gravity Well", 1 << 2, 0)]
    [InlineData("Incantation of Imperil Other", 1 << 3, 0)]
    [InlineData("Magic Yield Other VI", 1 << 4, 0)]
    [InlineData("Incantation of Fire Vulnerability Other", 1 << 5, 5)]
    [InlineData("Acid Lure VI", 1 << 5, 6)]
    [InlineData("Incantation of Blade Lure", 1 << 5, 1)]
    [InlineData("Incantation of Bludgeon Lure", 1 << 5, 3)]
    [InlineData("Incantation of Frost Lure", 1 << 5, 4)]
    [InlineData("Incantation of Flame Lure", 1 << 5, 5)]
    [InlineData("Incantation of Lightning Lure", 1 << 5, 7)]
    [InlineData("Incantation of Piercing Lure", 1 << 5, 2)]
    [InlineData("Weakening Curse VII", 1 << 9, 0)]
    [InlineData("Festering Curse VII", 1 << 10, 0)]
    [InlineData("Corruption VII", 1 << 11, 0)]
    [InlineData("Destructive Curse VII", 1 << 12, 0)]
    [InlineData("Corrosion VII", 1 << 13, 0)]
    public void ClassifierMapsEveryVtankMonsterDebuffColumn(
        string name,
        int expectedFlag,
        int expectedDamage)
    {
        Assert.True(DebuffSpellCatalog.TryClassify(
            Spell(1, name),
            out DebuffIdentity identity,
            out _));
        Assert.Equal((MonsterActionFlags)expectedFlag, identity.Flag);
        Assert.Equal((MonsterDamageType)expectedDamage, identity.DamageType);
    }

    [Fact]
    public void DamageAndExtraVulnerabilityBothProduceCandidates()
    {
        DebuffSpellCatalog catalog = DebuffSpellCatalog.Build(
        [
            Spell(1, "Incantation of Fire Vulnerability Other"),
            Spell(2, "Incantation of Acid Vulnerability Other"),
        ]);
        var actions = new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Vulnerability,
            DamageType = MonsterDamageType.Fire,
            ExtraVulnerability = MonsterDamageType.Acid,
        };

        IReadOnlyList<DebuffChoice> choices = catalog.Candidates(
            actions,
            DebuffSelectionMethod.SpellLevel,
            new Character(),
            static (_, _) => true);

        Assert.Equal(2, choices.Count);
        Assert.Contains(choices, choice =>
            choice.Identity.DamageType == MonsterDamageType.Fire);
        Assert.Contains(choices, choice =>
            choice.Identity.DamageType == MonsterDamageType.Acid);
    }

    [Fact]
    public void LureBladeItemSpellIsNotAClassicVulnerabilityLure()
    {
        Assert.False(DebuffSpellCatalog.TryClassify(
            Spell(1, "Incantation of Lure Blade"),
            out _,
            out _));
    }

    [Fact]
    public void SkillPreferenceUsesBuffedSchoolSkillBeforeSpellTier()
    {
        DebuffSpellCatalog catalog = DebuffSpellCatalog.Build(
        [
            Spell(1, "Imperil Other VII", tier: 7, school: 33),
            Spell(2, "Weakening Curse VI", tier: 6, school: 43),
        ]);
        var actions = new MonsterRuleActions
        {
            Flags = MonsterActionFlags.Imperil | MonsterActionFlags.WeakeningCurse,
        };
        var character = new Character(
        [
            new PluginSkillInfo(33, "Life Magic", PluginSkillTraining.Trained, 300),
            new PluginSkillInfo(43, "Void Magic", PluginSkillTraining.Trained, 420),
        ]);

        IReadOnlyList<DebuffChoice> choices = catalog.Candidates(
            actions,
            DebuffSelectionMethod.Skill,
            character,
            static (_, _) => true);

        Assert.Equal(2u, choices[0].Spell.SpellId);
    }

    [Fact]
    public void TrackerWaitsForMatchingSuccessfulServerReceipt()
    {
        var tracker = new DebuffTracker();
        var identity = new DebuffIdentity(
            MonsterActionFlags.Imperil,
            MonsterDamageType.Auto);
        PluginSpellInfo spell = Spell(10, "Imperil Other VII", duration: 60);
        tracker.Begin(99, identity, spell, now: 10, completionRevision: 4);

        Assert.False(tracker.Observe(
            new PluginCastCompletion(5, 11, 99, 0), 12).Completed);
        Assert.True(tracker.HasPending);

        DebuffCompletion failed = tracker.Observe(
            new PluginCastCompletion(6, 10, 99, 0x0402), 13);
        Assert.True(failed.Completed);
        Assert.False(failed.Succeeded);
        Assert.True(tracker.IsDue(99, identity, spell, 13, 5));

        tracker.Begin(99, identity, spell, 14, completionRevision: 6);
        DebuffCompletion succeeded = tracker.Observe(
            new PluginCastCompletion(7, 10, 99, 0), 15);
        Assert.True(succeeded.Succeeded);
        Assert.False(tracker.IsDue(99, identity, spell, 69, 5));
        Assert.True(tracker.IsDue(99, identity, spell, 70, 5));
    }

    [Fact]
    public void DamageOverTimeDoesNotUsePrecastWindow()
    {
        var tracker = new DebuffTracker();
        var identity = new DebuffIdentity(
            MonsterActionFlags.Corrosion,
            MonsterDamageType.Auto);
        PluginSpellInfo spell = Spell(
            10,
            "Corrosion VII",
            duration: 60) with { IsDamageOverTime = true };
        tracker.Begin(99, identity, spell, 0, 0);
        tracker.Observe(new PluginCastCompletion(1, 10, 99, 0), 1);

        Assert.False(tracker.IsDue(99, identity, spell, 60, 20));
        Assert.True(tracker.IsDue(99, identity, spell, 61, 20));
    }

    [Fact]
    public void FakeImperilCreatesVtankThreeThousandSecondLocalMarker()
    {
        var tracker = new DebuffTracker();
        var identity = new DebuffIdentity(
            MonsterActionFlags.Imperil,
            MonsterDamageType.Auto);
        PluginSpellInfo learned = Spell(
            10,
            "Imperil Other VII",
            tier: 7,
            duration: 60);

        tracker.RecordFakeImperil(99u, now: 10d);

        Assert.False(tracker.IsDue(99u, identity, learned, 3009d, 0d));
        Assert.True(tracker.IsDue(99u, identity, learned, 3010d, 0d));
    }

    private static PluginSpellInfo Spell(
        uint id,
        string name,
        int tier = 8,
        uint school = 33,
        float duration = 30) => new(
            id,
            name,
            Family: id,
            Tier: tier,
            Difficulty: 300,
            ManaCost: 20,
            DurationSeconds: duration,
            School: school,
            Description: string.Empty,
            IsSelfTargeted: false,
            IsBeneficial: false)
        {
            IsDebuff = true,
            IsOffensive = true,
        };

    private sealed class Character(
        IReadOnlyList<PluginSkillInfo>? skills = null) : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public IReadOnlyList<PluginSkillInfo> Skills { get; } = skills ?? [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];

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
