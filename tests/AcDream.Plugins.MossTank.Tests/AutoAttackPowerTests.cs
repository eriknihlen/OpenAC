using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class AutoAttackPowerTests
{
    [Theory]
    [InlineData(1, 1, 0, false, false, 0.5f)]
    [InlineData(2, 1, 0, false, false, 0f)]
    [InlineData(2, 0, 0, false, false, 0.2f)]
    [InlineData(2, 0, 0x40, true, false, 0.49f)]
    [InlineData(2, 0, 0x40, false, true, 1f)]
    public void MeleeHybridDecisionTreeMatchesOfficialVtank(
        int requestedRaw,
        int weaponType,
        int attackType,
        bool dualWield,
        bool shield,
        float expected)
    {
        PluginInventoryItem weapon = Weapon(
            1, 1, damageType: 0x3, equippedLocation: 0x00100000) with
        {
            WeaponType = weaponType,
            AttackType = attackType,
        };
        var items = new List<PluginInventoryItem> { weapon };
        if (dualWield)
            items.Add(Weapon(2, 1, 0x1, 0x00200000));
        if (shield)
            items.Add(Weapon(3, 0x2, 0, 0x00200000));
        var settings = new CombatSettings { UseRecklessness = false };

        float actual = AutoAttackPower.Resolve(
            new MonsterRuleActions
            {
                DamageType = (MonsterDamageType)requestedRaw,
            },
            settings,
            new Character(),
            items);

        Assert.Equal(expected, actual, 2);
    }

    [Fact]
    public void MissileAndRecklessnessUseFullThenClampToRetailRange()
    {
        PluginInventoryItem bow = Weapon(1, 0x100, 0x2, 0x00400000);
        var settings = new CombatSettings
        {
            AutoAttackPower = true,
            UseRecklessness = true,
        };

        float power = AutoAttackPower.Resolve(
            new MonsterRuleActions { DamageType = MonsterDamageType.Pierce },
            settings,
            new Character(recklessness: true),
            [bow]);

        Assert.Equal(0.9f, power, 2);
    }

    [Fact]
    public void DisabledAutoPowerKeepsConfiguredValue()
    {
        var settings = new CombatSettings
        {
            AutoAttackPower = false,
            AttackPower = 0.37f,
        };
        Assert.Equal(
            0.37f,
            AutoAttackPower.Resolve(
                new MonsterRuleActions(),
                settings,
                new Character(),
                []),
            2);
    }

    private static PluginInventoryItem Weapon(
        uint id,
        uint itemType,
        int damageType,
        uint equippedLocation) => new(
            id, 0, "Weapon", itemType, 1, 0, 0, equippedLocation,
            0, 0, 0, 1, 0, 0, 0, 0, 0, 0, false, 0,
            0, damageType, 0, 0, 0, 0, 0);

    private sealed class Character(bool recklessness = false) : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1;
        public uint CurrentHealth => 100;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 100;
        public uint MaxStamina => 100;
        public uint CurrentMana => 100;
        public uint MaxMana => 100;
        public IReadOnlyList<PluginSkillInfo> Skills => recklessness
            ? [new(50, "Recklessness", PluginSkillTraining.Trained, 300)]
            : [];
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            if (skillId == 50 && recklessness)
            {
                skill = Skills[0];
                return true;
            }
            skill = default;
            return false;
        }
    }
}
