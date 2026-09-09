using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Core.Spells;

namespace AcDream.Core.Player;

public static class PlayerSkillMath
{
    public readonly record struct AugmentationBonuses(
        int AllSkills,
        bool JackOfAllTrades,
        int SkilledSpecialized,
        bool SkilledMelee,
        bool SkilledMissile,
        bool SkilledMagic)
    {
        public static AugmentationBonuses FromProperties(PropertyBundle properties)
        {
            ArgumentNullException.ThrowIfNull(properties);
            return new(
                Positive(properties.GetInt((uint)PropertyInt.LumAugAllSkills)),
                properties.GetInt((uint)PropertyInt.AugmentationJackOfAllTrades) > 0,
                Positive(properties.GetInt((uint)PropertyInt.LumAugSkilledSpec)),
                properties.GetInt((uint)PropertyInt.AugmentationSkilledMelee) > 0,
                properties.GetInt((uint)PropertyInt.AugmentationSkilledMissile) > 0,
                properties.GetInt((uint)PropertyInt.AugmentationSkilledMagic) > 0);
        }

        public int BeforeEnchantments(uint skillId)
        {
            int category = skillId switch
            {
                0x29u or 0x2Cu or 0x2Du or 0x2Eu or 0x31u when SkilledMelee => 10,
                0x2Fu when SkilledMissile => 10,
                0x1Fu or 0x20u or 0x21u or 0x22u or 0x2Bu when SkilledMagic => 10,
                _ => 0,
            };
            return SaturatingAdd(AllSkills, category);
        }

        public int AfterEnchantments(uint advancementClass)
        {
            int result = JackOfAllTrades ? 5 : 0;
            if (advancementClass == 3u)
                result = SaturatingAdd(result, SaturatingMultiply(SkilledSpecialized, 2));
            return result;
        }
    }

    public readonly record struct Value(
        int UnenchantedLevel,
        int EffectiveLevel,
        int VitaeModifier);

    public static Value Calculate(
        int intrinsicLevel,
        int enchantedIntrinsicLevel,
        uint skillId,
        uint advancementClass,
        AugmentationBonuses augmentations,
        EnchantmentMath.VitalMod enchantment,
        float vitaeMultiplier)
    {
        int before = augmentations.BeforeEnchantments(skillId);
        int unenchanted = SaturatingAdd(Math.Max(0, intrinsicLevel), before);
        // InqSkill(…, 0): the same augmentation prefix over the
        // enchanted-attribute intrinsic, then EnchantSkill, then the
        // post-enchantment augmentations.
        int enchantedBase = SaturatingAdd(Math.Max(0, enchantedIntrinsicLevel), before);
        int enchanted = EnchantmentMath.EnchantSkill(
            enchantment,
            (uint)enchantedBase);
        int effective = SaturatingAdd(
            enchanted,
            augmentations.AfterEnchantments(advancementClass));
        int vitaeModifier = EnchantmentMath.SkillVitaeModifier(
            vitaeMultiplier,
            (uint)unenchanted);
        return new Value(unenchanted, effective, vitaeModifier);
    }

    private static int Positive(int value) => value > 0 ? value : 0;

    private static int SaturatingAdd(int left, int right)
    {
        long result = (long)left + right;
        return result > int.MaxValue
            ? int.MaxValue
            : result < int.MinValue ? int.MinValue : (int)result;
    }

    private static int SaturatingMultiply(int left, int right)
    {
        long result = (long)left * right;
        return result > int.MaxValue
            ? int.MaxValue
            : result < int.MinValue ? int.MinValue : (int)result;
    }
}
