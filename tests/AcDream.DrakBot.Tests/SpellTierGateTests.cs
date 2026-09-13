using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class SpellTierGateTests
{
    private const uint LifeMagic = 33u;

    private static PluginSpellInfo Life(uint id, string name, int tier) =>
        Spell.SelfBuff(id, name, 100u, tier) with { School = LifeMagic };

    [Fact]
    public void TheLadderCapsTheTierByTheSchoolsSkill()
    {
        var minimums = new[] { 0, 85, 135, 185, 235, 285, 335, 435 };
        Assert.Equal(1, SpellTierGate.MaxTier(50u, minimums));
        Assert.Equal(2, SpellTierGate.MaxTier(85u, minimums));
        Assert.Equal(6, SpellTierGate.MaxTier(300u, minimums));
        Assert.Equal(8, SpellTierGate.MaxTier(500u, minimums));
    }

    [Fact]
    public void TheSelectorSkipsTiersTheSkillCannotCarryButNotUnknownSchools()
    {
        var surface = new FakeAutomationSurface();
        surface.Skills = [new PluginSkillInfo(LifeMagic, "Life Magic", PluginSkillTraining.Trained, 200u)];
        surface.SelfBuffs.Add(Life(1, "Heal Self IV", 4));
        surface.SelfBuffs.Add(Life(2, "Heal Self VI", 6));
        surface.SelfBuffs.Add(Life(3, "Heal Self VII", 7));
        surface.SelfBuffs.Add(Spell.SelfBuff(4, "Strength Self VII", 101u, 7)); // school unknown: not gated
        var settings = new SpellTierSettings { BuffMinimums = [0, 85, 135, 185, 235, 285, 335, 435] };
        var selector = new SpellSelector(surface, surface, null, new SpellTierGate(surface, () => settings));

        Assert.True(selector.TryBestSelfBuff("Heal Self", out PluginSpellInfo heal));
        Assert.Equal("Heal Self IV", heal.Name);
        Assert.True(selector.TryBestSelfBuff("Strength Self", out PluginSpellInfo strength));
        Assert.Equal("Strength Self VII", strength.Name);

        surface.Skills = [new PluginSkillInfo(LifeMagic, "Life Magic", PluginSkillTraining.Specialized, 340u)];
        Assert.True(selector.TryBestSelfBuff("Heal Self", out heal));
        Assert.Equal("Heal Self VII", heal.Name);
    }
}
