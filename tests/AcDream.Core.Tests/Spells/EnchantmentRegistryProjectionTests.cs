using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Spells;

public sealed class EnchantmentRegistryProjectionTests
{
    [Fact]
    public void GetEnchantmentsInEffect_ExcludesVitaeAndCooldown()
    {
        IReadOnlyList<ActiveEnchantmentRecord> result =
            EnchantmentRegistryProjection.GetEnchantmentsInEffect(
            [
                Enchantment(1u, 1u, category: 1u),
                Enchantment(2u, 4u, category: 2u),
                Enchantment(3u, 8u, category: 3u),
                Enchantment(4u, 2u, category: 4u),
            ]);

        Assert.Equal([1u, 4u], result.Select(record => record.SpellId));
    }

    [Fact]
    public void GetEnchantmentsInEffect_CategoryDuel_PrefersHigherPower()
    {
        IReadOnlyList<ActiveEnchantmentRecord> result =
            EnchantmentRegistryProjection.GetEnchantmentsInEffect(
            [
                Enchantment(1u, 1u, category: 12u, power: 1u),
                Enchantment(2u, 1u, category: 12u, power: 7u),
            ]);

        Assert.Equal(2u, Assert.Single(result).SpellId);
    }

    [Fact]
    public void GetEnchantmentsInEffect_PowerComparisonUsesRetailSignedInt()
    {
        IReadOnlyList<ActiveEnchantmentRecord> result =
            EnchantmentRegistryProjection.GetEnchantmentsInEffect(
            [
                Enchantment(1u, 1u, category: 12u, power: 1u),
                Enchantment(2u, 1u, category: 12u, power: uint.MaxValue),
            ]);

        Assert.Equal(1u, Assert.Single(result).SpellId);
    }

    [Fact]
    public void GetEnchantmentsInEffect_EqualPower_PrefersLaterStartAndKeepsExactTie()
    {
        IReadOnlyList<ActiveEnchantmentRecord> result =
            EnchantmentRegistryProjection.GetEnchantmentsInEffect(
            [
                Enchantment(1u, 1u, category: 12u, power: 7u, start: 10d),
                Enchantment(2u, 1u, category: 12u, power: 7u, start: 20d),
                Enchantment(3u, 2u, category: 12u, power: 7u, start: 20d),
            ]);

        Assert.Equal(2u, Assert.Single(result).SpellId);
    }

    [Fact]
    public void GetEnchantmentsInEffect_ReplacingWinner_AppendsInRetailOrder()
    {
        IReadOnlyList<ActiveEnchantmentRecord> result =
            EnchantmentRegistryProjection.GetEnchantmentsInEffect(
            [
                Enchantment(1u, 1u, category: 10u, power: 1u),
                Enchantment(2u, 1u, category: 20u, power: 1u),
                Enchantment(3u, 2u, category: 10u, power: 2u),
            ]);

        Assert.Equal([2u, 3u], result.Select(record => record.SpellId));
    }

    private static ActiveEnchantmentRecord Enchantment(
        uint spellId,
        uint bucket,
        uint category,
        uint power = 1u,
        double start = 0d)
        => new(
            SpellId: spellId,
            LayerId: spellId,
            Duration: 60d,
            CasterGuid: 1u,
            Bucket: bucket,
            StartTime: start,
            SpellCategory: category,
            PowerLevel: power);
}
