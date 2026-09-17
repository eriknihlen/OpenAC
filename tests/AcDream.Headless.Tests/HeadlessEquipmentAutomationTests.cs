using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

public sealed class HeadlessEquipmentAutomationTests
{
    private const uint Player = 0x50000001u;
    private const uint Wand = 0x50000A01u;

    [Fact]
    public void TryEquip_OwnedWandSendsTheGuardedWield()
    {
        var h = new Harness();
        h.AddOwnedItem(Wand, EquipMask.Held);

        Assert.True(h.Automation.TryEquip(Wand, (uint)EquipMask.Held));

        Assert.Equal(new[] { (Wand, (uint)EquipMask.Held) }, h.Wields);
    }

    [Fact]
    public void TryEquip_RefusedSendLeavesTheControllerNotBusy()
    {
        var h = new Harness();
        h.AddOwnedItem(Wand, EquipMask.Held);
        h.SendWieldSucceeds = false;

        Assert.False(h.Automation.TryEquip(Wand, (uint)EquipMask.Held));

        Assert.False(h.AutoWield.IsBusy);
    }

    [Fact]
    public void TryEquip_RefusesWhileATransactionIsBusy()
    {
        var h = new Harness();
        h.AddOwnedItem(Wand, EquipMask.Held);
        h.Runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.False(h.Automation.TryEquip(Wand, (uint)EquipMask.Held));

        Assert.Empty(h.Wields);
    }

    [Fact]
    public void ExplicitCombatModeRequest_ForwardsToTheBoundAutoWieldController()
    {
        var h = new Harness();
        h.Combat.SetCombatMode(CombatMode.Missile);
        const uint bow = 0x50000B01u;
        h.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = bow,
            Type = ItemType.MissileWeapon,
            ValidLocations = EquipMask.MissileWeapon,
        });
        h.Runtime.InventoryOwner.Objects.MoveItem(bow, Player, -1, EquipMask.MissileWeapon);
        h.AddOwnedItem(Wand, EquipMask.Held);

        Assert.True(h.AutoWield.TryWield(
            h.Runtime.InventoryOwner.Objects.Get(Wand)!,
            EquipMask.Held));
        h.Combat.SetCombatMode(CombatMode.NonCombat);
        Assert.True(h.Runtime.InventoryOwner.Objects.ApplyConfirmedServerMove(
            bow, Player, 0u, 0));
        Assert.True(h.Runtime.InventoryOwner.Objects.ApplyConfirmedServerWield(
            Wand, Player, EquipMask.Held));
        h.Combat.SetCombatMode(CombatMode.Magic);

        h.Gameplay.NotifyExplicitCombatModeRequest();
        h.Combat.SetCombatMode(CombatMode.NonCombat);

        Assert.Empty(h.CombatModeRequests);
    }

    private sealed class NoOpTransport : IRuntimeInteractionTransport
    {
        public bool IsInWorld => true;

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            sequence = 0u;
            return false;
        }

        public bool TrySendPickup(
            uint itemGuid, uint destinationContainerId, int placement, out uint sequence)
        {
            sequence = 0u;
            return false;
        }
    }

    private sealed class Harness
    {
        internal readonly GameRuntime Runtime;
        internal readonly HeadlessGameplayOperations Gameplay;
        internal readonly AutoWieldController AutoWield;
        internal readonly HeadlessItemAutomation Automation;
        internal readonly List<(uint Item, uint Mask)> Wields = [];
        internal readonly List<CombatMode> CombatModeRequests = [];
        internal bool SendWieldSucceeds = true;

        internal CombatState Combat => Runtime.ActionOwner.Combat;

        internal Harness()
        {
            Gameplay = new HeadlessGameplayOperations();
            Runtime = new GameRuntime(new GameRuntimeDependencies(
                Gameplay, Gameplay, Gameplay, Gameplay));
            Gameplay.Bind(Runtime, catalog: null, () => "account");
            Runtime.PlayerIdentity.ServerGuid = Player;
            AutoWield = new AutoWieldController(
                Runtime.InventoryOwner.Objects,
                () => Runtime.PlayerIdentity.ServerGuid,
                (item, mask) =>
                {
                    Wields.Add((item, mask));
                    return SendWieldSucceeds;
                },
                (item, container, placement) => true,
                combatState: Combat,
                sendChangeCombatMode: CombatModeRequests.Add,
                transactions: Runtime.InventoryOwner.Transactions);
            Gameplay.BindAutoWield(AutoWield);
            Automation = new HeadlessItemAutomation(
                Runtime,
                new NoOpTransport(),
                (item, container, placement) => true,
                (item, container, placement, amount) => true,
                (source, target, amount) => true,
                (source, target) => true,
                item => true,
                (item, amount) => true,
                (target, item, amount) => true,
                isComponentPack: null,
                autoWield: AutoWield);
        }

        internal void AddOwnedItem(uint id, EquipMask validLocations) =>
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = id,
                Type = ItemType.Caster,
                ContainerId = Player,
                ValidLocations = validLocations,
            });
    }
}
