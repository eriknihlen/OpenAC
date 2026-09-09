using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank;

namespace AcDream.Plugins.MossTank.Tests;

public class BuffPlanTests
{
    private const uint LifeMagicSkill = 33u;
    private const uint CreatureEnchantmentSkill = 31u;

    private static readonly BuffSettings Default = new();

    private static PluginSpellInfo Spell(
        uint id, uint family, int tier, string description,
        int difficulty = 50, int mana = 10, uint school = CreatureEnchantmentSkill,
        float duration = 1800f) =>
        new(id, $"spell-{id}", family, tier, difficulty, mana, duration, school,
            description, IsSelfTargeted: true, IsBeneficial: true);

    private static PluginSkillInfo Skill(
        uint id, string name, PluginSkillTraining training, uint level = 300) =>
        new(id, name, training, level);

    private static PluginAttributeInfo Attribute(int kind, string name) =>
        new(kind, name, 200);

    private static List<BuffLine> Lines(params PluginSpellInfo[] spells) =>
        BuffProfile.Build(spells);


    [Theory]
    [InlineData("Increases the caster's Life Magic skill by 10 points.",
        BuffTargetKind.Skill, "Life Magic")]
    [InlineData("Increases the caster's Strength by 10 points.",
        BuffTargetKind.Attribute, "Strength")]
    [InlineData("Increases the caster's Self by 10 points.",
        BuffTargetKind.Attribute, "Self")]
    [InlineData("Increases the caster's Assess Monster skill by 10 points.",
        BuffTargetKind.Skill, "Assess Creature")]
    public void ParsesBuffTargetFromDescription(
        string description, BuffTargetKind expectedKind, string expectedTarget)
    {
        Assert.True(BuffProfile.TryParseTarget(description, out var kind, out string target));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedTarget, target);
    }

    [Theory]
    [InlineData("Reduces damage the caster takes from Fire by 9%.",
        BuffTargetKind.Protection)]
    [InlineData("Increases the caster's natural armor by 20 points.",
        BuffTargetKind.Protection)]
    [InlineData("Increases a weapon's damage value by 2 points.", BuffTargetKind.Aura)]
    [InlineData("Improves a weapon's speed by 10 points.", BuffTargetKind.Aura)]
    [InlineData("Increases the Melee Defense skill modifier of a weapon or magic caster by 3%.",
        BuffTargetKind.Aura)]
    [InlineData("Increases the elemental damage bonus of an elemental magic caster by 1%.",
        BuffTargetKind.Aura)]
    public void ClassifiesProtectionsAndAuras(string description, BuffTargetKind expected)
    {
        BuffProfile.Classify(description, out BuffTargetKind kind, out _);
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void ProtectionsAreSelfCandidatesAndAurasAreNot()
    {
        var lines = Lines(
            Spell(1, 109, 1, "Reduces damage the caster takes from Fire by 9%."),
            Spell(2, 154, 1, "Increases a weapon's damage value by 2 points."));

        var all = BuffPlan.Build(lines, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(), Default);
        Assert.Equal([1u], all.Select(static spell => spell.SpellId));

        var none = BuffPlan.Build(lines, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(),
            new BuffSettings { BuffProtections = false });
        Assert.Empty(none);
    }

    [Fact]
    public void BanesAreClassifiedFromRetailsTargetYourselfInstruction()
    {
        const string bane =
            "Increases a shield or piece of armor's resistance to slashing damage by 10%. "
            + "Target yourself to cast this spell on all of your equipped armor.";

        BuffProfile.Classify(bane, out BuffTargetKind kind, out _);
        Assert.Equal(BuffTargetKind.Bane, kind);
    }

    [Fact]
    public void BanesAreNeverSelfListCandidates()
    {
        var bane = new PluginSpellInfo(
            1, "Blade Bane I", Family: 174, Tier: 1, Difficulty: 50, ManaCost: 10,
            DurationSeconds: 1800f, School: CreatureEnchantmentSkill,
            Description: "Increases a shield or piece of armor's resistance to slashing "
                + "damage by 10%. Target yourself to cast this spell on all of your equipped armor.",
            IsSelfTargeted: false, IsBeneficial: true);
        var lines = BuffProfile.Build(new[] { bane });

        Assert.Equal(BuffTargetKind.Bane, lines[0].Kind);

        Assert.Empty(BuffPlan.Build(lines, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(), Default));
        Assert.Empty(BuffPlan.Build(lines, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(),
            new BuffSettings { BuffBanes = false }));
    }

    [Fact]
    public void ImpenetrabilityLeavesTheSelfListWithTheBanes()
    {
        var impenetrability = new PluginSpellInfo(
            2, "Impenetrability I", Family: 173, Tier: 1, Difficulty: 50,
            ManaCost: 10, DurationSeconds: 1800f, School: ItemEnchantmentSkill,
            Description: "Increases a shield or piece of armor's armor value by "
                + "20 points. Target yourself to cast this spell on all of your "
                + "equipped armor.",
            IsSelfTargeted: false, IsBeneficial: true);
        var armorSelf = new PluginSpellInfo(
            3, "Armor Self I", Family: 105, Tier: 1, Difficulty: 50, ManaCost: 10,
            DurationSeconds: 1800f, School: LifeMagicSkill,
            Description: "Increases the caster's natural armor by 20 points.",
            IsSelfTargeted: true, IsBeneficial: true);
        var lines = BuffProfile.Build(new[] { impenetrability, armorSelf });

        var plan = BuffPlan.Build(
            lines,
            [Skill(ItemEnchantmentSkill, "Item Enchantment", PluginSkillTraining.Trained)],
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(),
            Default);

        // Armor Self is eq.cs:159's own entry and stays; Impenetrability is
        // eq.cs:246's and does not.
        Assert.Equal([3u], plan.Select(static spell => spell.SpellId));
    }

    [Fact]
    public void UnrecognisedSelfBuffsFallIntoOtherAndAreOffByDefault()
    {
        var lines = Lines(
            Spell(1, 93, 1, "Restores 10 points of the caster's Health over 20 seconds."));

        Assert.Equal(BuffTargetKind.Other, lines[0].Kind);
        Assert.Empty(BuffPlan.Build(lines, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(), Default));
        Assert.Single(BuffPlan.Build(lines, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(),
            new BuffSettings { BuffOther = true }));
    }

    [Fact]
    public void IgnoresSpellsWhoseDescriptionSaysNothingAboutAStat()
    {
        // A vital transfer describes a drain, not a buff.
        Assert.False(BuffProfile.TryParseTarget(
            "Drains one-half of the caster's Stamina and gives 90% of that to his/her Mana.",
            out _, out _));
    }

    [Fact]
    public void ExcludesInstantaneousSpellsFromBuffLines()
    {
        // Vital transfers have no duration and share families across unrelated
        // lines, so treating them as buffs would be wrong twice over.
        var instant = Spell(1, family: 89, tier: 1,
            "Increases the caster's Strength by 10 points.", duration: 0f);
        Assert.Empty(BuffProfile.Build(new[] { instant }));
    }


    [Fact]
    public void BuffsTrainedSkillsAndSkipsUntrainedOnes()
    {
        var lines = Lines(
            Spell(1, 47, 1, "Increases the caster's Life Magic skill by 10 points."),
            Spell(2, 71, 1, "Increases the caster's Leadership skill by 10 points."));

        var plan = BuffPlan.Build(
            lines,
            new[]
            {
                Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Specialized),
                Skill(35, "Leadership", PluginSkillTraining.Untrained),
            },
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(),
            Default);

        Assert.Single(plan);
        Assert.Equal(1u, plan[0].SpellId);
    }

    [Fact]
    public void BuffsEveryAttribute()
    {
        var lines = Lines(
            Spell(1, 1, 1, "Increases the caster's Strength by 10 points."),
            Spell(2, 11, 1, "Increases the caster's Self by 10 points."));

        var plan = BuffPlan.Build(
            lines,
            Array.Empty<PluginSkillInfo>(),
            new[] { Attribute(0, "Strength"), Attribute(5, "Self") },
            Array.Empty<PluginActiveEnchantment>(),
            Default);

        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void PicksTheStrongestTierTheCastingSkillCanCarry()
    {
        // Skill 300; tiers at difficulty 400 and 200. With the default excess
        // of 10, only the 200 tier is reliable.
        var lines = Lines(
            Spell(1, 47, 7, "Increases the caster's Life Magic skill by 10 points.",
                difficulty: 400, school: LifeMagicSkill),
            Spell(2, 47, 4, "Increases the caster's Life Magic skill by 10 points.",
                difficulty: 200, school: LifeMagicSkill));

        var plan = BuffPlan.Build(
            lines,
            new[] { Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained, 300) },
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(),
            Default);

        Assert.Single(plan);
        Assert.Equal(2u, plan[0].SpellId);
    }

    [Fact]
    public void SkipsAFamilyAlreadyInForceAtAnEqualTierWithTimeLeft()
    {
        var lines = Lines(
            Spell(1, 47, 4, "Increases the caster's Life Magic skill by 10 points.",
                difficulty: 100, school: LifeMagicSkill));

        var plan = BuffPlan.Build(
            lines,
            new[] { Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained) },
            Array.Empty<PluginAttributeInfo>(),
            new[] { new PluginActiveEnchantment(1, 47, 4, 900) },
            Default);

        Assert.Empty(plan);
    }

    [Fact]
    public void RecastsWhenTheBuffInForceIsWeaker()
    {
        var lines = Lines(
            Spell(2, 47, 7, "Increases the caster's Life Magic skill by 10 points.",
                difficulty: 100, school: LifeMagicSkill));

        var plan = BuffPlan.Build(
            lines,
            new[] { Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained) },
            Array.Empty<PluginAttributeInfo>(),
            new[] { new PluginActiveEnchantment(1, 47, 2, 900) },
            Default);

        Assert.Single(plan);
    }

    [Fact]
    public void RefreshesBelowVirindiTanksFiveMinuteThreshold()
    {
        var lines = Lines(
            Spell(1, 47, 4, "Increases the caster's Life Magic skill by 10 points.",
                difficulty: 100, school: LifeMagicSkill));
        var skills = new[] { Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained) };

        var comfortable = BuffPlan.Build(lines, skills, Array.Empty<PluginAttributeInfo>(),
            new[] { new PluginActiveEnchantment(1, 47, 4, 301) }, Default);
        var expiring = BuffPlan.Build(lines, skills, Array.Empty<PluginAttributeInfo>(),
            new[] { new PluginActiveEnchantment(1, 47, 4, 299) }, Default);

        Assert.Empty(comfortable);
        Assert.Single(expiring);
    }

    [Fact]
    public void OrdersCheapestFirstSoAPartialPassLandsMoreBuffs()
    {
        var lines = Lines(
            Spell(1, 1, 1, "Increases the caster's Strength by 10 points.", mana: 500),
            Spell(2, 3, 1, "Increases the caster's Endurance by 10 points.", mana: 5),
            Spell(3, 5, 1, "Increases the caster's Quickness by 10 points.", mana: 50));

        var plan = BuffPlan.Build(
            lines, Array.Empty<PluginSkillInfo>(),
            new[] { Attribute(0, "Strength"), Attribute(1, "Endurance"), Attribute(2, "Quickness") },
            Array.Empty<PluginActiveEnchantment>(), Default);

        Assert.Equal(new uint[] { 2, 3, 1 }, plan.Select(s => s.SpellId).ToArray());
    }

    [Fact]
    public void ForceQueuesBuffsThatAreAlreadyInForce()
    {
        // Virindi Tank's Force Buff recasts everything rather than only what
        // has lapsed, which is what the Buff button does.
        var lines = Lines(
            Spell(1, 47, 4, "Increases the caster's Life Magic skill by 10 points.",
                difficulty: 100, school: LifeMagicSkill));
        var skills = new[] { Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained) };
        var active = new[] { new PluginActiveEnchantment(1, 47, 4, 1800) };

        Assert.Empty(BuffPlan.Build(lines, skills, Array.Empty<PluginAttributeInfo>(),
            active, Default));
        Assert.Single(BuffPlan.Build(lines, skills, Array.Empty<PluginAttributeInfo>(),
            active, Default, force: true));
    }

    [Fact]
    public void ForceStillRespectsSkillAndTrainingFilters()
    {
        // Forcing means "ignore what is already up", not "ignore the settings".
        var lines = Lines(
            Spell(1, 71, 1, "Increases the caster's Leadership skill by 10 points."));

        var plan = BuffPlan.Build(
            lines,
            new[] { Skill(35, "Leadership", PluginSkillTraining.Untrained) },
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(),
            Default, force: true);

        Assert.Empty(plan);
    }

    [Fact]
    public void EmptySpellbookProducesNoPlan()
    {
        Assert.Empty(BuffPlan.Build(
            Array.Empty<BuffLine>(), Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(), Array.Empty<PluginActiveEnchantment>(),
            Default));
    }


    private const uint ItemEnchantmentSkill = 32u;

    private static List<BuffLine> OrderingSpellbook() => Lines(
        Spell(1, 101, 1, "Increases the caster's Creature Enchantment skill by 10 points.",
            mana: 90, school: CreatureEnchantmentSkill),
        Spell(2, 102, 1, "Increases the caster's Focus by 10 points.",
            mana: 80, school: CreatureEnchantmentSkill),
        Spell(3, 103, 1, "Increases the caster's Self by 10 points.",
            mana: 70, school: CreatureEnchantmentSkill),
        Spell(4, 104, 1, "Increases the caster's Endurance by 10 points.",
            mana: 60, school: CreatureEnchantmentSkill),
        Spell(5, 105, 1, "Increases the caster's Strength by 10 points.",
            mana: 50, school: CreatureEnchantmentSkill),
        Spell(6, 106, 1, "Increases the caster's Life Magic skill by 10 points.",
            mana: 40, school: CreatureEnchantmentSkill),
        Spell(7, 107, 1, "Increases the caster's maximum burden by 100 units.",
            mana: 30, school: ItemEnchantmentSkill),
        Spell(8, 108, 1, "Increases a weapon's damage value by 2 points.",
            mana: 20, school: ItemEnchantmentSkill),
        // Life Magic (school 33)
        Spell(9, 109, 1, "Reduces damage the caster takes from Fire by 9%.",
            mana: 10, school: LifeMagicSkill));

    private static List<PluginSpellInfo> OrderedPlan() =>
        BuffPlan.Build(
            OrderingSpellbook(),
            new[]
            {
                Skill(CreatureEnchantmentSkill, "Creature Enchantment",
                    PluginSkillTraining.Specialized),
                Skill(ItemEnchantmentSkill, "Item Enchantment", PluginSkillTraining.Trained),
                Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained),
            },
            new[]
            {
                Attribute(0, "Strength"), Attribute(1, "Endurance"),
                Attribute(4, "Focus"), Attribute(5, "Self"),
            },
            Array.Empty<PluginActiveEnchantment>(),
            new BuffSettings { BuffOther = true },
            force: true);

    [Fact]
    public void CreatureSpellsCastFirst_ThenItem_ThenLifeLast()
    {
        List<PluginSpellInfo> plan = OrderedPlan();

        int lastCreature = plan.FindLastIndex(s => s.School == CreatureEnchantmentSkill);
        int firstItem = plan.FindIndex(s => s.School == ItemEnchantmentSkill);
        int lastItem = plan.FindLastIndex(s => s.School == ItemEnchantmentSkill);
        int firstLife = plan.FindIndex(s => s.School == LifeMagicSkill);

        Assert.True(lastCreature < firstItem,
            "creature spells must all be cast before item spells");
        Assert.True(lastItem < firstLife,
            "item spells must all be cast before life spells");
        Assert.Equal(plan.Count - 1, firstLife);   // the protection goes last
    }

    [Fact]
    public void CreatureGroupLeadsWithMagicSkill_ThenFocus_Willpower_Endurance()
    {
        List<PluginSpellInfo> plan = OrderedPlan();

        Assert.Equal(new uint[] { 1, 2, 3, 4 }, plan.Take(4).Select(s => s.SpellId));

        Assert.Equal(
            new uint[] { 5, 6 },
            plan.Skip(4)
                .Where(s => s.School == CreatureEnchantmentSkill)
                .Select(s => s.SpellId)
                .OrderBy(id => id));
    }

    [Fact]
    public void WithinOneGroupTheCheapestStillCastsFirst()
    {
        List<PluginSpellInfo> plan = OrderedPlan();
        List<PluginSpellInfo> tail = plan
            .Skip(4)
            .Where(s => s.School == CreatureEnchantmentSkill)
            .ToList();

        Assert.Equal(2, tail.Count);
        Assert.True(tail[0].ManaCost <= tail[1].ManaCost);
        Assert.Equal(6u, tail[0].SpellId);   // the 40-mana line before the 50-mana one
    }

    // ── The vital regeneration rates ─────────────────────────────────────

    [Theory]
    [InlineData("Increase caster's natural healing rate by 10%.", "Health")]
    [InlineData("Increases your Health Regeneration Rate by 50%. "
        + "This effect can be layered with normal spell effects.", "Health")]
    [InlineData("Increases the rate at which the caster regains Stamina by 10%.", "Stamina")]
    [InlineData("Increases your Stamina Regeneration Rate by 50%.", "Stamina")]
    [InlineData("Increases the caster's natural mana rate by 10%.", "Mana")]
    [InlineData("Increases your Mana Regeneration Rate by 50%.", "Mana")]
    public void RegenerationRatesAreClassifiedPerVital(string description, string vital)
    {
        BuffProfile.Classify(description, out var kind, out string target);
        Assert.Equal(BuffTargetKind.Regeneration, kind);
        Assert.Equal(vital, target);
    }

    [Fact]
    public void HermeticLinkIsAWandAura()
    {
        BuffProfile.Classify(
            "Increases a magic casting implement's mana conversion bonus by 10%.",
            out var kind, out _);
        Assert.Equal(BuffTargetKind.Aura, kind);
    }

    [Fact]
    public void RegenerationRatesFinishThePass()
    {
        List<BuffLine> book = Lines(
            Spell(1, 201, 1, "Increases the caster's Focus by 10 points.",
                mana: 10, school: CreatureEnchantmentSkill),
            Spell(2, 202, 1, "Reduces damage the caster takes from Fire by 9%.",
                mana: 10, school: LifeMagicSkill),
            Spell(3, 203, 1, "Increases the caster's natural armor by 20 points.",
                mana: 10, school: LifeMagicSkill),
            Spell(4, 204, 1, "Increase caster's natural healing rate by 10%.",
                mana: 1, school: LifeMagicSkill),
            Spell(5, 205, 1, "Increases the rate at which the caster regains Stamina by 10%.",
                mana: 1, school: LifeMagicSkill),
            Spell(6, 206, 1, "Increases the caster's natural mana rate by 10%.",
                mana: 1, school: LifeMagicSkill));

        List<PluginSpellInfo> plan = BuffPlan.Build(
            book,
            new[]
            {
                Skill(CreatureEnchantmentSkill, "Creature Enchantment",
                    PluginSkillTraining.Specialized),
                Skill(LifeMagicSkill, "Life Magic", PluginSkillTraining.Trained),
            },
            new[] { Attribute(4, "Focus") },
            Array.Empty<PluginActiveEnchantment>(),
            new BuffSettings { BuffOther = true },
            force: true);

        Assert.Equal(6, plan.Count);
        // ...the protections precede them...
        Assert.Equal(new uint[] { 2, 3 }, plan.Skip(1).Take(2).Select(s => s.SpellId));
        Assert.Equal(
            new uint[] { 4, 5, 6 },
            plan.TakeLast(3).Select(s => s.SpellId).OrderBy(id => id));
    }

    [Fact]
    public void RegenerationRatesCanBeSwitchedOff()
    {
        List<BuffLine> book = Lines(
            Spell(1, 301, 1, "Increase caster's natural healing rate by 10%.",
                school: LifeMagicSkill));
        var off = new BuffSettings { BuffRegeneration = false };

        Assert.Single(BuffPlan.Build(book, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(), Default, force: true));
        Assert.Empty(BuffPlan.Build(book, Array.Empty<PluginSkillInfo>(),
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(), off, force: true));
    }


    private static readonly PluginSpellComponentSet MasterySelf =
        new(7u, 25u, 42u, 60u);   // Hyssop, Powdered Agate, Gypsum, Rowan
    /// <summary>The same family's OTHER line: only the talisman differs.</summary>
    private static readonly PluginSpellComponentSet MasteryOther =
        new(7u, 25u, 42u, 49u);   // ... Poplar

    private static PluginSpellInfo Tier(
        uint id,
        string name,
        int tier,
        int difficulty,
        PluginSpellComponentSet componentSet,
        bool self = true,
        bool fellowship = false,
        bool untargeted = false,
        uint school = CreatureEnchantmentSkill) =>
        new(id, name, 43u, tier, difficulty, 10, 1800f, school,
            "Increases the caster's Creature Enchantment skill by 10 points.",
            IsSelfTargeted: self, IsBeneficial: true)
        {
            IsFellowship = fellowship,
            IsUntargeted = untargeted,
            ComponentSet = componentSet,
        };

    private static List<PluginSpellInfo> PlanFor(
        uint creatureSkill,
        params PluginSpellInfo[] book) =>
        BuffPlan.Build(
            Lines(book),
            new[]
            {
                Skill(CreatureEnchantmentSkill, "Creature Enchantment",
                    PluginSkillTraining.Specialized, creatureSkill),
                Skill(LifeMagicSkill, "Life Magic",
                    PluginSkillTraining.Specialized, 450),
            },
            Array.Empty<PluginAttributeInfo>(),
            Array.Empty<PluginActiveEnchantment>(),
            new BuffSettings { BuffOther = true },
            force: true);

    [Fact]
    public void AGemSpellCarryingTheOtherLinesCompSetIsNeverATierCandidate()
    {
        List<PluginSpellInfo> plan = PlanFor(
            creatureSkill: 350u,
            Tier(0x022Du, "Creature Enchantment Mastery Self I", 1, 1, MasterySelf),
            Tier(0x0232u, "Creature Enchantment Mastery Self VI", 6, 250, MasterySelf),
            Tier(0x08A6u, "Adja's Boon", 7, 300, MasteryOther, self: false),
            Tier(0x11B2u, "Incantation of Creature Enchantment Mastery Self",
                8, 400, MasterySelf));

        Assert.Single(plan);
        Assert.Equal(0x0232u, plan[0].SpellId);
    }

    [Fact]
    public void TheSelfGemOfTheSameFamilyStaysACandidate()
    {
        List<PluginSpellInfo> plan = PlanFor(
            creatureSkill: 350u,
            Tier(0x022Du, "Creature Enchantment Mastery Self I", 1, 1, MasterySelf),
            Tier(0x08A7u, "Adja's Blessing", 7, 300, MasterySelf));

        Assert.Single(plan);
        Assert.Equal(0x08A7u, plan[0].SpellId);
    }

    [Fact]
    public void TheOtherLinesIncantationIsNotATierCandidateAtTheSameQuality()
    {
        List<PluginSpellInfo> plan = PlanFor(
            creatureSkill: 450u,
            Tier(0x022Du, "Creature Enchantment Mastery Self I", 1, 1, MasterySelf),
            Tier(0x11B1u, "Incantation of Creature Enchantment Mastery Other",
                8, 400, MasteryOther, self: false),
            Tier(0x11B2u, "Incantation of Creature Enchantment Mastery Self",
                8, 400, MasterySelf));

        Assert.Single(plan);
        Assert.Equal(0x11B2u, plan[0].SpellId);
    }

    [Fact]
    public void AFellowshipMemberOfTheFamilyIsNotATierCandidate()
    {
        List<PluginSpellInfo> plan = PlanFor(
            creatureSkill: 450u,
            Tier(0x022Du, "Creature Enchantment Mastery Self I", 1, 1, MasterySelf),
            Tier(0x0D3Bu, "Superior Conjurant Chant", 7, 325, MasterySelf,
                fellowship: true));

        Assert.Single(plan);
        Assert.Equal(0x022Du, plan[0].SpellId);
    }

    /// <summary>
    /// <c>A_0.isUntargeted == item.isUntargeted</c> -- the term that keeps a
    /// targeted line from resolving to an untargeted ring of the same family.
    /// </summary>
    [Fact]
    public void AnUntargetedMemberOfATargetedLineIsNotATierCandidate()
    {
        List<PluginSpellInfo> plan = PlanFor(
            creatureSkill: 450u,
            Tier(0x022Du, "Creature Enchantment Mastery Self I", 1, 1, MasterySelf),
            Tier(0x0999u, "Mastery Ring", 7, 325, MasterySelf, untargeted: true));

        Assert.Single(plan);
        Assert.Equal(0x022Du, plan[0].SpellId);
    }

    [Fact]
    public void ACrossSchoolMemberOfTheFamilyIsNotATierCandidate()
    {
        List<PluginSpellInfo> plan = PlanFor(
            creatureSkill: 450u,
            Tier(0x022Du, "Creature Enchantment Mastery Self I", 1, 1, MasterySelf),
            Tier(0x0998u, "Life Impostor", 7, 325, MasterySelf,
                school: LifeMagicSkill));

        Assert.Single(plan);
        Assert.Equal(0x022Du, plan[0].SpellId);
    }

    [Fact]
    public void TheLineReferenceIsTheWeakestSelfMemberAndClassifiesTheLine()
    {
        var other = new PluginSpellInfo(
            0x0001u, "Strength Other I", 1u, 1, 1, 10, 1800f,
            CreatureEnchantmentSkill,
            "Increases the target's Strength by 10 points.",
            IsSelfTargeted: false, IsBeneficial: true);
        var selfSix = new PluginSpellInfo(
            0x0534u, "Strength Self VI", 1u, 6, 250, 10, 1800f,
            CreatureEnchantmentSkill,
            "Increases the caster's Strength by 10 points.",
            IsSelfTargeted: true, IsBeneficial: true);
        var selfOne = new PluginSpellInfo(
            0x0002u, "Strength Self I", 1u, 1, 1, 10, 1800f,
            CreatureEnchantmentSkill,
            "Increases the caster's Strength by 10 points.",
            IsSelfTargeted: true, IsBeneficial: true);

        List<BuffLine> lines = Lines(other, selfSix, selfOne);

        BuffLine line = Assert.Single(lines);
        Assert.Equal(0x0002u, line.Reference.SpellId);
        Assert.Equal(BuffTargetKind.Attribute, line.Kind);
        Assert.Equal("Strength", line.TargetName);
    }
}
