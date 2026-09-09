using AcDream.Core.Combat;
using AcDream.Core.Player;
using AcDream.UI.Abstractions.Panels.Vitals;

namespace AcDream.UI.Abstractions.Tests;

public sealed class VitalsVMTests
{
    [Fact]
    public void HealthPercent_ReturnsCombatStateValue_AfterUpdateHealth()
    {
        var combat = new CombatState();
        uint guid = 0x5000_0042u;
        combat.OnUpdateHealth(guid, 0.42f);

        var vm = new VitalsVM(combat);
        vm.SetLocalPlayerGuid(guid);

        Assert.Equal(0.42f, vm.HealthPercent, precision: 3);
    }

    [Fact]
    public void HealthPercent_LocalPrivateVitalOverridesStaleCombatPercent()
    {
        var combat = new CombatState();
        uint guid = 0x5000_0042u;
        combat.OnUpdateHealth(guid, 1f);

        var local = new LocalPlayerState();
        local.OnVitalUpdate(vitalId: 2u, ranks: 50u, start: 50u, xp: 0u, current: 1u);
        var vm = new VitalsVM(combat, local);
        vm.SetLocalPlayerGuid(guid);

        Assert.Equal(0.01f, vm.HealthPercent, precision: 3);
    }

    [Fact]
    public void HealthPercent_ReadsCurrentOnlyPrivateVitalDeltaWithoutStaleCache()
    {
        var local = new LocalPlayerState();
        local.OnVitalUpdate(vitalId: 2u, ranks: 50u, start: 50u, xp: 0u, current: 100u);
        var vm = new VitalsVM(new CombatState(), local);

        local.OnVitalCurrent(vitalId: 2u, current: 25u);

        Assert.Equal(0.25f, vm.HealthPercent, precision: 3);
    }

    [Fact]
    public void HealthPercent_ReturnsOne_WhenGuidUnknown()
    {
        var combat = new CombatState();
        var vm = new VitalsVM(combat);

        Assert.Equal(1f, vm.HealthPercent);
    }

    [Fact]
    public void HealthPercent_ReturnsOne_WhenGuidSetButNeverUpdated()
    {
        var combat = new CombatState();
        var vm = new VitalsVM(combat);
        vm.SetLocalPlayerGuid(0xDEAD_BEEFu);

        Assert.Equal(1f, vm.HealthPercent);
    }

    [Fact]
    public void StaminaPercent_IsNull_WhenNoLocalPlayerStateProvided()
    {
        var vm = new VitalsVM(new CombatState());
        Assert.Null(vm.StaminaPercent);
    }

    [Fact]
    public void ManaPercent_IsNull_WhenNoLocalPlayerStateProvided()
    {
        var vm = new VitalsVM(new CombatState());
        Assert.Null(vm.ManaPercent);
    }

    [Fact]
    public void StaminaPercent_FromLocalPlayerState_AfterVitalUpdate()
    {
        var local = new LocalPlayerState();
        local.OnVitalUpdate(vitalId: 4u, ranks: 50u, start: 50u, xp: 0u, current: 80u);

        var vm = new VitalsVM(new CombatState(), local);

        Assert.Equal(0.8f, vm.StaminaPercent!.Value, precision: 3);
    }

    [Fact]
    public void ManaPercent_FromLocalPlayerState_AfterVitalUpdate()
    {
        var local = new LocalPlayerState();
        local.OnVitalUpdate(vitalId: 6u, ranks: 20u, start: 80u, xp: 0u, current: 25u);

        var vm = new VitalsVM(new CombatState(), local);

        Assert.Equal(0.25f, vm.ManaPercent!.Value, precision: 3);
    }

    [Fact]
    public void Vm_ReadsThroughToLocalPlayerState_NoStaleCache()
    {
        var local = new LocalPlayerState();
        var vm = new VitalsVM(new CombatState(), local);

        Assert.Null(vm.StaminaPercent); // no data yet

        local.OnVitalUpdate(vitalId: 4u, ranks: 50u, start: 50u, xp: 0u, current: 50u);
        local.OnVitalUpdate(vitalId: 6u, ranks: 50u, start: 50u, xp: 0u, current: 50u);

        Assert.Equal(0.5f, vm.StaminaPercent!.Value, precision: 3);
        Assert.Equal(0.5f, vm.ManaPercent!.Value, precision: 3);
    }

    [Fact]
    public void SetLocalPlayerGuid_ReroutesHealthLookup_WithoutStaleCache()
    {
        var combat = new CombatState();
        uint playerGuid = 0x5003_E219u;
        combat.OnUpdateHealth(playerGuid, 0.75f);

        var vm = new VitalsVM(combat);
        // Before SetLocalPlayerGuid — reads GUID=0 → returns safe 1.0.
        Assert.Equal(1f, vm.HealthPercent);

        vm.SetLocalPlayerGuid(playerGuid);
        Assert.Equal(0.75f, vm.HealthPercent, precision: 3);
    }

    [Fact]
    public void Constructor_ThrowsOnNullCombat()
    {
        Assert.Throws<ArgumentNullException>(() => new VitalsVM(null!));
    }
}
