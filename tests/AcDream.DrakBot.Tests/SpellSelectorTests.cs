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

    [Theory]
    [InlineData("Pierce Protection Self", "Piercing Protection Self VI")]
    [InlineData("Bludgeon Protection Self", "Bludgeoning Protection Self VI")]
    [InlineData("Flame Protection Self", "Fire Protection Self VI")]
    [InlineData("Frost Protection Self", "Cold Protection Self VI")]
    [InlineData("Fire Protection Self", "Fire Protection Self VI")]
    [InlineData("Blood Drinker Self", "Aura of Blood Drinker Self VI")]
    [InlineData("Swift Killer Self", "Aura of Swift Killer Self VI")]
    public void TheOldNamesForTheProtectionsFindTheBooksSpelling(string asked, string book)
    {
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(6, book, 30, 6));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff(asked, out PluginSpellInfo spell));
        Assert.Equal(6u, spell.SpellId);
    }

    [Fact]
    public void ANumberedSiblingUnderAnotherNameIsNotATierOfTheFamily()
    {
        // Healing Mastery Self VI shares Heal Self's family in the spell
        // table and is not a heal. "Heal Self" is the heal; the family
        // reach is for the lore-named seventh, which carries no numeral.
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(5, "Heal Self V", 20, 5));
        surface.SelfBuffs.Add(Spell.SelfBuff(6, "Healing Mastery Self VI", 20, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(7, "Adja's Intervention", 20, 7));
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff("Heal Self", out PluginSpellInfo spell));
        Assert.Equal(7u, spell.SpellId);
        surface.MissingComponents.Add(7u);
        selector = new SpellSelector(surface, surface);
        Assert.True(selector.TryBestSelfBuff("Heal Self", out spell));
        Assert.Equal(5u, spell.SpellId);
        Assert.Equal("casts Heal Self V", selector.Explain("Heal Self"));
    }

    [Fact]
    public void ASelfBuffNeverResolvesToItsOtherTier()
    {
        // Self and Other tiers share a family in the spell table. With the
        // book holding Strength Other VI and only Strength Self III with
        // components, "Strength Self" is the third, not the Other - which
        // wants a target and never lands on the caster.
        var surface = new FakeAutomationSurface();
        surface.SelfBuffs.Add(Spell.SelfBuff(3, "Strength Self III", 10, 3));
        surface.SelfBuffs.Add(Spell.SelfBuff(6, "Strength Self VI", 10, 6));
        surface.SelfBuffs.Add(Spell.SelfBuff(16, "Strength Other VI", 10, 6) with { IsSelfTargeted = false });
        surface.MissingComponents.Add(6u);
        var selector = new SpellSelector(surface, surface);

        Assert.True(selector.TryBestSelfBuff("Strength Self", out PluginSpellInfo spell));
        Assert.Equal(3u, spell.SpellId);
        Assert.Equal("casts Strength Self III", selector.Explain("Strength Self"));
        Assert.True(selector.TryBestSelfBuff("Strength Other", out spell));
        Assert.Equal(16u, spell.SpellId);
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
