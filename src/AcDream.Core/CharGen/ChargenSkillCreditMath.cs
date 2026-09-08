namespace AcDream.Core.CharGen;

public static class ChargenSkillCreditMath
{
    public static int ComputeSpent(
        ChargenSkillAdvancementSet advancement,
        IReadOnlyDictionary<uint, ChargenSkillCost> costsBySkillId,
        IReadOnlyDictionary<uint, ChargenSkillCost> globalCostsBySkillId)
    {
        ArgumentNullException.ThrowIfNull(advancement);
        ArgumentNullException.ThrowIfNull(costsBySkillId);
        ArgumentNullException.ThrowIfNull(globalCostsBySkillId);

        int spent = 0;
        for (uint skillId = 1; skillId < ChargenSkillAdvancementSet.SlotCount; skillId++)
        {
            ChargenSkillAdvancementClass cls = advancement[skillId];
            if (cls != ChargenSkillAdvancementClass.Trained
                && cls != ChargenSkillAdvancementClass.Specialized)
            {
                continue;
            }

            if (!costsBySkillId.TryGetValue(skillId, out ChargenSkillCost cost)
                && !globalCostsBySkillId.TryGetValue(skillId, out cost))
            {
                continue;
            }

            spent += cls == ChargenSkillAdvancementClass.Specialized
                ? cost.PrimaryCost
                : cost.NormalCost;
        }
        return spent;
    }

    public static int RemainingCredits(
        uint totalSkillCredits,
        ChargenSkillAdvancementSet advancement,
        IReadOnlyDictionary<uint, ChargenSkillCost> costsBySkillId,
        IReadOnlyDictionary<uint, ChargenSkillCost> globalCostsBySkillId) =>
        checked((int)totalSkillCredits) - ComputeSpent(advancement, costsBySkillId, globalCostsBySkillId);
}
