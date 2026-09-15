using AcDream.DrakBot.Spells;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

public sealed class SpellSelectorTests
{
    [Theory]
    [InlineData("Strength Self I", "Strength Self")]
    [InlineData("Strength Self VIII", "Strength Self")]
    [InlineData("Heal Self IV", "Heal Self")]
    [InlineData("Flame Bolt V", "Flame Bolt")]
    [InlineData("Ring of True Pain", "Ring of True Pain")]
    [InlineData("  Armor Self II ", "Armor Self")]
    public void BaseNameDropsTheTierSuffix(string name, string expected) =>
        Assert.Equal(expected, SpellSelector.BaseName(name));

    [Fact]
    public void PicksTheHighestKnownTierOfAFamily()
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(1, "Strength Self I", 10, 1));
        surface.SelfBuffs.Add(Spell.SelfBuff(6, "Strength Self VI", 10, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(3, "Strength Self III", 10, 3));
        surface.SelfBuffs.Add(Spell.SelfBuff(20, "Endurance Self VII", 11, 7));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff("Strength Self", out PluginSpellInfo spell));
        Assert.Equal(6u, spell.SpellId);
    }

    [Fact]
    public void TheLoreNamedSeventhAndTheIncantationAreReachedThroughTheFamily()
    {
        // The book holds tiers I-VI under the plain name, the seventh under
        // its lore name and the eighth as an Incantation: the user still
        // just says "Strength Self" and gets the top one the tiers allow.
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(1, "Strength Self I", 10, 1));
        surface.SelfBuffs.Add(Spell.SelfBuff(6, "Strength Self VI", 10, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(7, "Bulwark of Strength", 10, 7));
        surface.SelfBuffs.Add(Spell.SelfBuff(8, "Incantation of Strength Self", 10, 8));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff("Strength Self", out PluginSpellInfo spell));
        Assert.Equal(8u, spell.SpellId);
        Assert.Equal("casts Incantation of Strength Self", selector.Explain("Strength Self"));

        // No scarabs for the top two: the family falls to the sixth.
        surface.MissingComponents.Add(8u);
        surface.MissingComponents.Add(7u);
        selector = new SpellSelector(surface, surface);
        Assert.True(selector.TryBestSelfBuff("Strength Self", out spell));
        Assert.Equal(6u, spell.SpellId);

        // Nothing of the family has components: said so, not guessed.
        surface.MissingComponents.Add(6u);
        surface.MissingComponents.Add(1u);
        selector = new SpellSelector(surface, surface);
        Assert.False(selector.TryBestSelfBuff("Strength Self", out _));
        Assert.Equal("4 tier(s) known, no components for any", selector.Explain("Strength Self"));
        Assert.Equal("not in the spellbook", selector.Explain("Quickness Self"));
    }

    [Fact]
    public void ABookHoldingOnlyTheLoreNamedSeventhStillAnswersToThePlainName()
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(7, "Might of the Lugians", 10, 7));
        surface.SelfBuffs.Add(Spell.SelfBuff(17, "Blessing of the Mace Turner", 12, 7));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff("Strength Self", out PluginSpellInfo spell));
        Assert.Equal(7u, spell.SpellId);
        Assert.True(selector.TryBestSelfBuff("Bludgeon Protection Self", out spell));
        Assert.Equal(17u, spell.SpellId);
        Assert.False(selector.TryBestSelfBuff("Focus Self", out _));
    }

    [Fact]
    public void SkipsTiersWithoutComponents()
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(3, "Strength Self III", 10, 3));
        surface.SelfBuffs.Add(Spell.SelfBuff(6, "Strength Self VI", 10, 6));
        surface.MissingComponents.Add(6);
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff("strength self", out PluginSpellInfo spell));
        Assert.Equal(3u, spell.SpellId);
    }

    [Fact]
    public void UnknownFamilyIsReportedNotGuessed()
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(3, "Strength Self III", 10, 3));
        var selector = new SpellSelector(surface, surface);

        Assert.False(selector.TryBestSelfBuff("Endurance Self", out _));
    }

    [Fact]
    public void BestAttackHonoursTheElementKeyword()
    {
        var surface = new FakeAutomationSurface();
        surface.AttackSpells.Add(Spell.Attack(100, "Flame Bolt VI", 6));
        surface.AttackSpells.Add(Spell.Attack(101, "Frost Bolt V", 5));
        surface.AttackSpells.Add(Spell.Attack(102, "Frost Bolt VII", 7));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestAttack("frost", out PluginSpellInfo frost));
        Assert.Equal(102u, frost.SpellId);
        Assert.True(selector.TryBestAttack(null, out PluginSpellInfo any));
        Assert.Equal(102u, any.SpellId);
        Assert.False(selector.TryBestAttack("acid", out _));
    }
}
