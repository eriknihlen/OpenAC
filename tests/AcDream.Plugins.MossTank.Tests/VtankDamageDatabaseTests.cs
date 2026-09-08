using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankDamageDatabaseTests
{
    [Fact]
    public void ExactMonsterOverrideWinsOverSpecies()
    {
        PluginCombatTarget target = Target("Magma Golem", 1);

        Assert.Equal(
            [
                MonsterDamageType.Cold,
                MonsterDamageType.Bludgeon,
                MonsterDamageType.Pierce,
                MonsterDamageType.Slash,
            ],
            VtankDamageDatabase.Preferences(target));
    }

    [Fact]
    public void CreatureTypeUsesOfficialOrderedSpeciesPreference()
    {
        PluginCombatTarget target = Target("Some Olthoi", 1);

        Assert.Equal(MonsterDamageType.Bludgeon,
            VtankDamageDatabase.Preferences(target)[0]);
        Assert.True(
            VtankDamageDatabase.PreferenceIndex(
                target,
                MonsterDamageType.Bludgeon)
            < VtankDamageDatabase.PreferenceIndex(
                target,
                MonsterDamageType.Fire));
    }

    [Fact]
    public void UnknownTargetUsesVtankFinalFallbackOrder()
    {
        PluginCombatTarget target = Target("New Server Creature", 0);

        Assert.Equal(
            [
                MonsterDamageType.Pierce,
                MonsterDamageType.Bludgeon,
                MonsterDamageType.Slash,
                MonsterDamageType.Acid,
                MonsterDamageType.Electric,
                MonsterDamageType.Cold,
                MonsterDamageType.Fire,
            ],
            VtankDamageDatabase.Preferences(target));
    }

    private static PluginCombatTarget Target(string name, int species) =>
        new(10, name, 100, 5, 0, true, 1f) { SpeciesId = species };
}
