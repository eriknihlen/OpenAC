using AcDream.App.Combat;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Combat;

public sealed class CharacterOptionCombatSettingsSourceTests
{
    [Fact]
    public void ReadsLiveBits_NotAConstructionTimeSnapshot()
    {
        var options = new RuntimeCharacterOptionsState();
        options.SetOptionBit((uint)CharacterOptionId.AutoTarget, false);
        options.SetOptionBit((uint)CharacterOptionId.AutoRepeatAttack, false);
        options.SetOptionBit((uint)CharacterOptionId.ViewCombatTarget, false);
        var source = new CharacterOptionCombatSettingsSource(options);

        Assert.False(source.AutoTarget);
        Assert.False(source.AutoRepeatAttack);
        Assert.False(source.ViewCombatTarget);

        options.SetOptionBit((uint)CharacterOptionId.AutoTarget, true);
        Assert.True(source.AutoTarget);
        Assert.False(source.AutoRepeatAttack);
        Assert.False(source.ViewCombatTarget);

        options.SetOptionBit((uint)CharacterOptionId.AutoRepeatAttack, true);
        options.SetOptionBit((uint)CharacterOptionId.ViewCombatTarget, true);
        Assert.True(source.AutoRepeatAttack);
        Assert.True(source.ViewCombatTarget);
    }

    [Fact]
    public void ReflectsClientDefaults_ForAFreshCharacterOptionsState()
    {
        var options = new RuntimeCharacterOptionsState();
        var source = new CharacterOptionCombatSettingsSource(options);

        Assert.True(source.AutoTarget);
        Assert.True(source.AutoRepeatAttack);
        Assert.False(source.ViewCombatTarget);
    }

    [Fact]
    public void Constructor_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(
            static () => new CharacterOptionCombatSettingsSource(null!));
    }
}
