using AcDream.Core.Items;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Player;

/// <summary>
/// The whole chain that turns what the server sends into the skill number a
/// player — or a bot — reads: the attribute-derived term, the ranks bought
/// with experience, the permanent bonuses from augmentations, then spells,
/// then the death penalty. The numbers below are a maxed character: both
/// attributes behind the skill at 315, so its attribute term is 315, and
/// 208 ranks is the practical ceiling for a trained skill.
/// </summary>
public sealed class SkillLevelConformanceTests
{
    private const uint SkillId = 24u;
    private const uint Trained = 2u;
    private const uint Specialized = 3u;
    private const uint AttributeTerm = 315u;

    [Fact]
    public void TrainedAtTheCeilingReadsTheAttributeTermPlusItsRanks()
    {
        LocalPlayerState state = Player();
        Train(state, ranks: 208u, init: 0u, status: Trained);

        PlayerSkillMath.Value value = state.GetSkillValue(SkillId)!.Value;

        // 315 attribute term + 0 starting value + 208 ranks.
        Assert.Equal(523, value.UnenchantedLevel);
        Assert.Equal(523, value.EffectiveLevel);
    }

    [Fact]
    public void SpecializedCarriesItsHigherStartingValueAndRankCeiling()
    {
        LocalPlayerState state = Player();
        Train(state, ranks: 226u, init: 10u, status: Specialized);

        PlayerSkillMath.Value value = state.GetSkillValue(SkillId)!.Value;

        // 315 + 10 + 226.
        Assert.Equal(551, value.UnenchantedLevel);
        Assert.Equal(551, value.EffectiveLevel);
    }

    [Theory]
    [InlineData(208u, 0u, Trained, 528)]
    [InlineData(226u, 10u, Specialized, 556)]
    public void TheAllSkillsAugmentationAddsFiveBeforeAnythingElse(
        uint ranks,
        uint init,
        uint status,
        int expected)
    {
        LocalPlayerState state = Player();
        Train(state, ranks, init, status);

        PlayerSkillMath.Value value =
            state.GetSkillValue(SkillId, AllSkillsAugmentation())!.Value;

        Assert.Equal(expected, value.UnenchantedLevel);
        Assert.Equal(expected, value.EffectiveLevel);
    }

    [Fact]
    public void SpellsScaleTheSkillFirstAndAddToItSecond()
    {
        var book = new Spellbook(SpellTable.Create(
            [TestSpell(1u), TestSpell(2u)]));
        book.OnEnchantmentAdded(SkillEnchantment(
            spellId: 1u, layerId: 1u, value: 1.15f, bucket: 1u));
        book.OnEnchantmentAdded(SkillEnchantment(
            spellId: 2u, layerId: 2u, value: 20f, bucket: 2u));
        LocalPlayerState state = Player(book);
        Train(state, ranks: 208u, init: 0u, status: Trained);

        PlayerSkillMath.Value value = state.GetSkillValue(SkillId)!.Value;

        // 523 unchanged underneath; 523 x 1.15 = 601.45, + 20 = 621.45.
        Assert.Equal(523, value.UnenchantedLevel);
        Assert.Equal(621, value.EffectiveLevel);
    }

    [Fact]
    public void TheDeathPenaltyScalesTheSkillTheAugmentationHasAlreadyRaised()
    {
        var book = new Spellbook(SpellTable.Create([TestSpell(1u)]));
        book.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 1u,
            LayerId: 1u,
            Duration: 0d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Multiplicative,
            StatModKey: null,
            StatModValue: 0.95f,
            Bucket: 4u));
        LocalPlayerState state = Player(book);
        Train(state, ranks: 208u, init: 0u, status: Trained);

        PlayerSkillMath.Value value =
            state.GetSkillValue(SkillId, AllSkillsAugmentation())!.Value;

        // The augmentation is inside the number the penalty scales:
        // 523 + 5 = 528, and 528 x 0.95 = 501.6.
        Assert.Equal(528, value.UnenchantedLevel);
        Assert.Equal(501, value.EffectiveLevel);
        Assert.Equal(-27, value.VitaeModifier);
    }

    // ── Fixture ───────────────────────────────────────────────────────────

    /// <summary>
    /// A character whose two attributes behind this skill are both at 315,
    /// so the skill's attribute term is 315.
    /// </summary>
    private static LocalPlayerState Player(Spellbook? spellbook = null)
    {
        var state = new LocalPlayerState(spellbook)
        {
            SkillFormulaBonusResolver = (skillId, status, attrs) =>
                skillId == SkillId ? AttributeTerm : 0u,
        };
        state.OnAttributeUpdate(atType: 1u, ranks: 215u, start: 100u, xp: 0u);
        state.OnAttributeUpdate(atType: 4u, ranks: 215u, start: 100u, xp: 0u);
        return state;
    }

    private static void Train(
        LocalPlayerState state,
        uint ranks,
        uint init,
        uint status) =>
        state.OnSkillWireUpdate(
            skillId: SkillId,
            ranks: ranks,
            status: status,
            xp: 0u,
            init: init,
            resistance: 0u,
            lastUsed: 0d);

    /// <summary>The permanent +5 to every skill.</summary>
    private static PropertyBundle AllSkillsAugmentation()
    {
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.LumAugAllSkills] = 5;
        return properties;
    }

    private static ActiveEnchantmentRecord SkillEnchantment(
        uint spellId,
        uint layerId,
        float value,
        uint bucket) =>
        new(
            SpellId: spellId,
            LayerId: layerId,
            Duration: 60d,
            CasterGuid: 0u,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Skill
                | (uint)(bucket == 1u
                    ? EnchantmentMath.EnchantmentTypeFlag.Multiplicative
                    : EnchantmentMath.EnchantmentTypeFlag.Additive),
            StatModKey: SkillId,
            StatModValue: value,
            Bucket: bucket,
            SpellCategory: spellId);

    private static SpellMetadata TestSpell(uint spellId) => new(
        spellId, "Test", "War Magic", 0u, 0u, "", 0f, 0,
        false, false, "", 0, 0, 0u, 0, false, false, true,
        0f, 0u, 0u, 0u, 0);
}
