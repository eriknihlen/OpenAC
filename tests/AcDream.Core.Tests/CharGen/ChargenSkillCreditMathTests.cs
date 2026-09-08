using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenSkillCreditMathTests
{
    private static readonly Dictionary<uint, ChargenSkillCost> Costs = new()
    {
        [1u] = new ChargenSkillCost(1u, NormalCost: 4, PrimaryCost: 12), // Axe
        [11u] = new ChargenSkillCost(11u, NormalCost: 4, PrimaryCost: 12), // Sword
        [24u] = new ChargenSkillCost(24u, NormalCost: 1, PrimaryCost: 3), // Run
        // Deliberately no entry for skill id 2 (Bow) — heritage doesn't offer it.
    };

    /// <summary>Empty global fallback — tests that only exercise the
    /// heritage tier pass this so a miss is a genuine both-tiers miss.</summary>
    private static readonly Dictionary<uint, ChargenSkillCost> NoGlobalCosts = new();

    [Fact]
    public void ComputeSpent_IgnoresInactiveAndUntrainedSkills()
    {
        var advancement = new ChargenSkillAdvancementSet
        {
            [1u] = ChargenSkillAdvancementClass.Inactive,
            [11u] = ChargenSkillAdvancementClass.Untrained,
        };

        Assert.Equal(0, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, NoGlobalCosts));
    }

    [Fact]
    public void ComputeSpent_ChargesNormalCostForTrainedSkills()
    {
        var advancement = new ChargenSkillAdvancementSet { [1u] = ChargenSkillAdvancementClass.Trained };

        Assert.Equal(4, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, NoGlobalCosts));
    }

    [Fact]
    public void ComputeSpent_ChargesPrimaryCostInsteadOfNormalCostForSpecializedSkills()
    {
        var advancement = new ChargenSkillAdvancementSet { [1u] = ChargenSkillAdvancementClass.Specialized };

        Assert.Equal(12, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, NoGlobalCosts));
    }

    [Fact]
    public void ComputeSpent_SumsAcrossMultipleTrainedAndSpecializedSkills()
    {
        var advancement = new ChargenSkillAdvancementSet
        {
            [1u] = ChargenSkillAdvancementClass.Trained,       // 4
            [11u] = ChargenSkillAdvancementClass.Specialized,  // 12
            [24u] = ChargenSkillAdvancementClass.Trained,      // 1
        };

        Assert.Equal(17, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, NoGlobalCosts));
    }

    [Fact]
    public void ComputeSpent_SkillWithNoCostEntryInEitherTierIsSkipped()
    {
        var advancement = new ChargenSkillAdvancementSet { [2u] = ChargenSkillAdvancementClass.Trained };

        Assert.Equal(0, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, NoGlobalCosts));
    }

    [Fact]
    public void ComputeSpent_HeritageCostWinsOverGlobalCostWhenBothPresent()
    {
        var globalCosts = new Dictionary<uint, ChargenSkillCost>
        {
            [1u] = new ChargenSkillCost(1u, NormalCost: 999, PrimaryCost: 999),
        };
        var advancement = new ChargenSkillAdvancementSet { [1u] = ChargenSkillAdvancementClass.Trained };

        Assert.Equal(4, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, globalCosts));
    }

    [Fact]
    public void ComputeSpent_FallsBackToGlobalCostWhenHeritageListHasNoEntry()
    {
        var globalCosts = new Dictionary<uint, ChargenSkillCost>
        {
            [2u] = new ChargenSkillCost(2u, NormalCost: 6, PrimaryCost: 18),
        };
        var advancement = new ChargenSkillAdvancementSet
        {
            [2u] = ChargenSkillAdvancementClass.Trained,
        };

        Assert.Equal(6, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, globalCosts));
    }

    [Fact]
    public void ComputeSpent_FallsBackToGlobalCostForSpecializedSkillsToo()
    {
        var globalCosts = new Dictionary<uint, ChargenSkillCost>
        {
            [2u] = new ChargenSkillCost(2u, NormalCost: 6, PrimaryCost: 18),
        };
        var advancement = new ChargenSkillAdvancementSet
        {
            [2u] = ChargenSkillAdvancementClass.Specialized,
        };

        Assert.Equal(18, ChargenSkillCreditMath.ComputeSpent(advancement, Costs, globalCosts));
    }

    [Fact]
    public void RemainingCredits_IsTotalMinusSpent_AndMayGoNegativeUnlikeAttributes()
    {
        var advancement = new ChargenSkillAdvancementSet
        {
            [1u] = ChargenSkillAdvancementClass.Trained,
            [11u] = ChargenSkillAdvancementClass.Specialized,
        };

        Assert.Equal(84, ChargenSkillCreditMath.RemainingCredits(100u, advancement, Costs, NoGlobalCosts));
        Assert.Equal(-16, ChargenSkillCreditMath.RemainingCredits(0u, advancement, Costs, NoGlobalCosts));
    }
}
