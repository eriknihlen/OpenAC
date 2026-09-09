using AcDream.Core.Items;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Player;

public sealed class PlayerSkillMathTests
{
    [Fact]
    public void Calculate_PreservesRetailAugmentationOrdering()
    {
        var augmentations = new PlayerSkillMath.AugmentationBonuses(
            AllSkills: 3,
            JackOfAllTrades: true,
            SkilledSpecialized: 4,
            SkilledMelee: false,
            SkilledMissile: false,
            SkilledMagic: true);

        PlayerSkillMath.Value value = PlayerSkillMath.Calculate(
            intrinsicLevel: 100,
            enchantedIntrinsicLevel: 100,
            skillId: 0x1Fu,
            advancementClass: 3u,
            augmentations,
            enchantment: new EnchantmentMath.VitalMod(0.5f, 0f),
            vitaeMultiplier: 0.8f);

        Assert.Equal(113, value.UnenchantedLevel); // 100 + all 3 + magic 10
        Assert.Equal(69, value.EffectiveLevel);    // trunc(113 * .5) + JOAT 5 + spec 8
        Assert.Equal(-23, value.VitaeModifier);    // trunc(113 * .8) - 113
    }

    [Fact]
    public void Calculate_EnchantedIntrinsicFeedsOnlyTheEffectiveLevel()
    {
        var augmentations = new PlayerSkillMath.AugmentationBonuses(
            AllSkills: 0,
            JackOfAllTrades: false,
            SkilledSpecialized: 0,
            SkilledMelee: false,
            SkilledMissile: false,
            SkilledMagic: true);

        PlayerSkillMath.Value value = PlayerSkillMath.Calculate(
            intrinsicLevel: 331,
            enchantedIntrinsicLevel: 353,
            skillId: 0x1Fu,
            advancementClass: 2u,
            augmentations,
            enchantment: new EnchantmentMath.VitalMod(1f, 50f),
            vitaeMultiplier: 1f);

        Assert.Equal(341, value.UnenchantedLevel); // 331 + magic 10
        Assert.Equal(413, value.EffectiveLevel);   // (353 + 10) + 50
        Assert.Equal(0, value.VitaeModifier);
    }

    [Theory]
    [InlineData(0x29u, true, false, false, 10)]
    [InlineData(0x2Fu, false, true, false, 10)]
    [InlineData(0x22u, false, false, true, 10)]
    [InlineData(0x18u, true, true, true, 0)]
    public void BeforeEnchantments_UsesRetailSkillCategorySwitch(
        uint skillId,
        bool melee,
        bool missile,
        bool magic,
        int expectedCategoryBonus)
    {
        var augmentations = new PlayerSkillMath.AugmentationBonuses(
            AllSkills: 2,
            JackOfAllTrades: false,
            SkilledSpecialized: 0,
            SkilledMelee: melee,
            SkilledMissile: missile,
            SkilledMagic: magic);

        Assert.Equal(
            2 + expectedCategoryBonus,
            augmentations.BeforeEnchantments(skillId));
    }

    [Fact]
    public void FromProperties_MapsNamedRetailQualities()
    {
        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.LumAugAllSkills] = 7;
        properties.Ints[(uint)PropertyInt.AugmentationJackOfAllTrades] = 1;
        properties.Ints[(uint)PropertyInt.LumAugSkilledSpec] = 3;
        properties.Ints[(uint)PropertyInt.AugmentationSkilledMissile] = 2;

        PlayerSkillMath.AugmentationBonuses value =
            PlayerSkillMath.AugmentationBonuses.FromProperties(properties);

        Assert.Equal(7, value.AllSkills);
        Assert.True(value.JackOfAllTrades);
        Assert.Equal(3, value.SkilledSpecialized);
        Assert.True(value.SkilledMissile);
        Assert.False(value.SkilledMelee);
        Assert.False(value.SkilledMagic);
    }
}
