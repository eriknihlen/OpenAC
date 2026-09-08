using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class VtankAmmunitionDatabaseTests
{
    [Fact]
    public void LoadsCompleteOfficialGameInfoTable()
    {
        Assert.Equal(120, VtankAmmunitionDatabase.Options.Count);
        Assert.Equal(5, VtankAmmunitionDatabase.LauncherType(0x001u));
        Assert.Equal(6, VtankAmmunitionDatabase.LauncherType(0x080u));
        Assert.Equal(7, VtankAmmunitionDatabase.LauncherType(0x020u));
    }

    [Fact]
    public void SpecialMaskSelectsRaiderOnlyWhenEnabled()
    {
        var character = new Character(
        [
            new PluginSkillInfo(
                47u,
                "Missile Weapons",
                PluginSkillTraining.Trained,
                300u)
            {
                Base = 300u,
            },
        ]);

        VtankAmmunitionOption regular = Assert.IsType<VtankAmmunitionOption>(
            VtankAmmunitionDatabase.Select(
                5,
                MonsterDamageType.Electric,
                VtankPrismaticAmmoPolicy.NoPrismatic,
                enabledSpecialMask: 0,
                character,
                static _ => true));
        VtankAmmunitionOption raider = Assert.IsType<VtankAmmunitionOption>(
            VtankAmmunitionDatabase.Select(
                5,
                MonsterDamageType.Electric,
                VtankPrismaticAmmoPolicy.NoPrismatic,
                enabledSpecialMask: 1,
                character,
                static _ => true));

        Assert.Equal("Deadly Lightning Arrow", regular.Name);
        Assert.Equal("Raider Lightning Arrow", raider.Name);
    }

    [Fact]
    public void PrismaticAmmoRequiresBothOfficialSkills()
    {
        var missileOnly = new Character(
        [
            new PluginSkillInfo(
                47u,
                "Missile Weapons",
                PluginSkillTraining.Trained,
                400u)
            {
                Base = 400u,
            },
        ]);
        var both = new Character(
        [
            new PluginSkillInfo(
                47u,
                "Missile Weapons",
                PluginSkillTraining.Trained,
                400u)
            {
                Base = 400u,
            },
            new PluginSkillInfo(
                37u,
                "Fletching",
                PluginSkillTraining.Trained,
                400u),
        ]);

        VtankAmmunitionOption withoutFletching =
            Assert.IsType<VtankAmmunitionOption>(
                VtankAmmunitionDatabase.Select(
                    5,
                    MonsterDamageType.Fire,
                    VtankPrismaticAmmoPolicy.ForcePrismatic,
                    0,
                    missileOnly,
                    static _ => true));
        VtankAmmunitionOption withFletching =
            Assert.IsType<VtankAmmunitionOption>(
                VtankAmmunitionDatabase.Select(
                    5,
                    MonsterDamageType.Fire,
                    VtankPrismaticAmmoPolicy.ForcePrismatic,
                    0,
                    both,
                    static _ => true));

        Assert.Equal("Deadly Fire Arrow", withoutFletching.Name);
        Assert.Equal("Deadly Prismatic Arrow", withFletching.Name);
    }

    private sealed class Character(IReadOnlyList<PluginSkillInfo> skills)
        : ICharacterInfo
    {
        public bool IsInWorld => true;
        public uint ObjectId => 1u;
        public uint CurrentHealth => 100u;
        public uint MaxHealth => 100u;
        public uint CurrentStamina => 100u;
        public uint MaxStamina => 100u;
        public uint CurrentMana => 100u;
        public uint MaxMana => 100u;
        public IReadOnlyList<PluginSkillInfo> Skills => skills;
        public IReadOnlyList<PluginAttributeInfo> Attributes => [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo value in skills)
            {
                if (value.SkillId != skillId)
                    continue;
                skill = value;
                return true;
            }
            skill = default;
            return false;
        }
    }
}
