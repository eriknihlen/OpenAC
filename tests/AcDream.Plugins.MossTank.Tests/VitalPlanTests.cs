using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank;

namespace AcDream.Plugins.MossTank.Tests;

public class VitalPlanTests
{
    private static readonly VitalSettings Default = new();

    private sealed class Character : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1u;
        public uint CurrentHealth { get; init; }
        public uint MaxHealth { get; init; } = 100;
        public uint CurrentStamina { get; init; }
        public uint MaxStamina { get; init; } = 100;
        public uint CurrentMana { get; init; }
        public uint MaxMana { get; init; } = 100;
        public IReadOnlyList<PluginSkillInfo> Skills { get; init; } = [];
        public IReadOnlyList<PluginAttributeInfo> Attributes { get; init; } = [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; init; } = [];
        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            skill = default;
            return false;
        }
    }

    private static PluginSpellInfo Spell(
        uint id, string name, int tier, int difficulty = 50) =>
        new(id, name, Family: 89, tier, difficulty, ManaCost: 0, DurationSeconds: 0f,
            School: 33u, Description: string.Empty,
            IsSelfTargeted: true, IsBeneficial: true);

    [Fact]
    public void ConvertsStaminaToManaWhenManaIsLowAndStaminaIsAvailable()
    {
        var character = new Character { CurrentMana = 10, CurrentStamina = 90 };
        Assert.Equal(VitalAction.StaminaToMana, VitalPlan.Decide(character, Default));
    }

    [Fact]
    public void RevitalizesWhenBothManaAndStaminaAreLow()
    {
        var character = new Character { CurrentMana = 10, CurrentStamina = 10 };
        Assert.Equal(VitalAction.Revitalize, VitalPlan.Decide(character, Default));
    }

    [Fact]
    public void DoesNothingWhenManaIsHealthy()
    {
        var character = new Character { CurrentMana = 95, CurrentStamina = 20 };
        Assert.Equal(VitalAction.None, VitalPlan.Decide(character, Default));
    }

    [Fact]
    public void DoesNothingWhenVitalsAreUnknown()
    {
        var character = new Character { MaxMana = 0, MaxStamina = 0 };
        Assert.Equal(VitalAction.None, VitalPlan.Decide(character, Default));
    }

    [Fact]
    public void PicksTheStrongestCastableConversionByName()
    {
        var known = new[]
        {
            Spell(1, "Stamina to Mana Self I", tier: 1),
            Spell(2, "Stamina to Mana Self VI", tier: 6),
            Spell(3, "Stamina to Health Self VII", tier: 7),
        };
        var levels = new Dictionary<uint, uint> { [33u] = 300 };

        Assert.True(VitalPlan.TryFind(
            known, VitalPlan.StaminaToManaStem, levels, 10, out PluginSpellInfo pick));
        Assert.Equal(2u, pick.SpellId);
    }

    [Fact]
    public void SkipsConversionTiersTheSkillCannotCarry()
    {
        var known = new[]
        {
            Spell(1, "Stamina to Mana Self I", tier: 1, difficulty: 50),
            Spell(2, "Stamina to Mana Self VII", tier: 7, difficulty: 400),
        };
        var levels = new Dictionary<uint, uint> { [33u] = 100 };

        Assert.True(VitalPlan.TryFind(
            known, VitalPlan.StaminaToManaStem, levels, 10, out PluginSpellInfo pick));
        Assert.Equal(1u, pick.SpellId);
    }

    [Fact]
    public void DoesNothingWhenVitalUpkeepIsTurnedOff()
    {
        var character = new Character { CurrentMana = 1, CurrentStamina = 100 };
        var off = new VitalSettings { Enabled = false };
        Assert.Equal(VitalAction.None, VitalPlan.Decide(character, off));
    }

    [Fact]
    public void ReportsNoConversionWhenTheCharacterKnowsNone()
    {
        var known = new[] { Spell(1, "Strength Self I", tier: 1) };
        Assert.False(VitalPlan.TryFind(
            known, VitalPlan.StaminaToManaStem, new Dictionary<uint, uint>(), 10, out _));
    }
}
