using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankDamageDatabaseTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "vtank",
        "gameinfodb-excerpt.ugd");

    private static VtankGameInfoDatabase Fixture() =>
        VtankGameInfoDatabase.Parse(File.ReadAllText(FixturePath));

    [Fact]
    public void ExactMonsterOverrideWinsOverSpecies()
    {
        Assert.Equal(
            [
                MonsterDamageType.Cold,
                MonsterDamageType.Bludgeon,
                MonsterDamageType.Pierce,
                MonsterDamageType.Slash,
            ],
            Fixture().DamagePreferences("Magma Golem"));
    }

    [Fact]
    public void SpeciesMembersResolveThroughSpeciesDamages()
    {
        VtankGameInfoDatabase database = Fixture();

        Assert.Equal(
            [
                MonsterDamageType.Bludgeon,
                MonsterDamageType.Pierce,
                MonsterDamageType.Slash,
                MonsterDamageType.Cold,
                MonsterDamageType.Fire,
                MonsterDamageType.Electric,
                MonsterDamageType.Acid,
            ],
            database.DamagePreferences("Olthoi Slasher"));
        Assert.Equal(
            database.DamagePreferences("Olthoi Slasher"),
            database.DamagePreferences("olthoi slasher"));
    }

    [Fact]
    public void UnknownMonsterHasNoAutoElementAtAll() =>
        Assert.Empty(Fixture().DamagePreferences("New Server Creature"));

    [Fact]
    public void NoFileMeansNoDataAtAll()
    {
        VtankGameInfoDatabase database = VtankGameInfoDatabase.Load(
            new EmptyStorage());

        Assert.False(database.IsLoaded);
        Assert.Empty(database.DamagePreferences("Magma Golem"));
        Assert.Empty(database.MonsterDamageOverrides);
        Assert.Empty(database.SpeciesMembers);
        Assert.Empty(database.SpeciesDamages);
        Assert.Empty(database.AmmunitionOptions);
        Assert.Empty(database.HealKits);
        Assert.Empty(database.GrenadeOptions);
        Assert.Empty(database.DrainSpellOptions);
        Assert.Empty(database.MartyrSpellOptions);
    }

    [Fact]
    public void AFileTheGrammarRejectsIsAlsoNoData()
    {
        VtankGameInfoDatabase database = VtankGameInfoDatabase.Load(
            new TextStorage("this is not a ugd"));

        Assert.False(database.IsLoaded);
        Assert.Empty(database.DamagePreferences("Magma Golem"));
    }

    [Fact]
    public void TheFileIsReadFromTheProfileDirectoryByItsRetailName()
    {
        var storage = new TextStorage(File.ReadAllText(FixturePath));

        VtankGameInfoDatabase database = VtankGameInfoDatabase.Load(storage);

        Assert.Equal("gameinfodb.ugd", storage.LastKey);
        Assert.True(database.IsLoaded);
    }

    [Fact]
    public void EveryTableShapeParses()
    {
        VtankGameInfoDatabase database = Fixture();

        // AmmoName, LauncherType, WieldReq, Element, Quality, Special,
        // WieldReq2Skill, WieldReq2Value.
        VtankAmmunitionOption ammo = database.AmmunitionOptions[0];
        Assert.Equal("Barbed Quarrel", ammo.Name);
        Assert.Equal(6, ammo.LauncherType);
        Assert.Equal(0, ammo.WieldRequirement);
        Assert.Equal(0, ammo.Element);
        Assert.Equal(4, ammo.Quality);

        // Monster, Species, MaximumHealth.
        Assert.Equal(
            new VtankSpeciesMember(1, 3190),
            database.SpeciesMembers["Olthoi Slasher"]);

        // KitName, RestoreBonus, SkillBonus, WhichVital.
        VtankHealKit kit = database.HealKits[0];
        Assert.Equal("Handy Healing Kit", kit.Name);
        Assert.Equal(1d, kit.RestoreBonus);
        Assert.Equal(50, kit.SkillBonus);
        Assert.Equal(1, kit.Vital);

        // GrenName, WieldReqType, WieldReqAttribute, WieldReqValue, Spell,
        // Spellcraft.
        VtankGrenadeOption grenade = database.GrenadeOptions[0];
        Assert.Equal("Iron Phial of Imperil", grenade.Name);
        Assert.Equal(2, grenade.WieldRequirementType);
        Assert.Equal(38, grenade.WieldRequirementAttribute);
        Assert.Equal(75, grenade.WieldRequirementValue);
        Assert.Equal(1323u, grenade.SpellId);
        Assert.Equal(100, grenade.Spellcraft);

        VtankDrainSpellOption drain = database.DrainSpellOptions[0];
        Assert.Equal(1237u, drain.SpellId);
        Assert.Equal(500, drain.CastTimeMilliseconds);
        Assert.Equal(0.25d, drain.EnemyDrainFactor);
        Assert.Equal(30, drain.EnemyDrainMaximumPoints);
        Assert.Equal(2d, drain.ResultMultiplier);

        VtankMartyrSpellOption martyr = database.MartyrSpellOptions[0];
        Assert.NotEqual(0u, martyr.SpellId);
        Assert.True(martyr.CastTimeMilliseconds > 0);
    }

    private sealed class EmptyStorage : IPluginStorage
    {
        public bool IsAvailable => true;
        public string? ReadText(string key) => null;
    }

    private sealed class TextStorage(string text) : IPluginStorage
    {
        public bool IsAvailable => true;
        public string? LastKey { get; private set; }

        public string? ReadText(string key)
        {
            LastKey = key;
            return text;
        }
    }
}
