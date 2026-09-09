using AcDream.Core.Combat;

namespace AcDream.Core.Net.Tests;

public sealed class CombatStateWiringTests
{
    [Theory]
    [InlineData((int)CombatMode.NonCombat)]
    [InlineData((int)CombatMode.Melee)]
    [InlineData((int)CombatMode.Missile)]
    [InlineData((int)CombatMode.Magic)]
    public void CombatModeProperty_appliesConcreteRetailMode(int value)
    {
        var combat = new CombatState();

        Assert.True(CombatStateWiring.ApplyPlayerIntProperty(
            combat,
            CombatStateWiring.CombatModePropertyId,
            value));

        Assert.Equal((CombatMode)value, combat.CurrentMode);
    }

    [Theory]
    [InlineData(39u, (int)CombatMode.Missile)]
    [InlineData(40u, (int)CombatMode.Undef)]
    [InlineData(40u, (int)CombatMode.ValidCombat)]
    [InlineData(40u, 0x10)]
    public void NonCombatQualityOrInvalidValue_isIgnored(uint property, int value)
    {
        var combat = new CombatState();

        Assert.False(CombatStateWiring.ApplyPlayerIntProperty(
            combat, property, value));

        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
    }
}
