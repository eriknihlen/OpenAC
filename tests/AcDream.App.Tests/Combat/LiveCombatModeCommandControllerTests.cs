using AcDream.App.Combat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Tests.Combat;

public sealed class LiveCombatModeCommandControllerTests
{
    [Fact]
    public void OutsideWorld_IsCompleteNoOpBeforeEquipmentQuery()
    {
        var harness = new Harness { Authority = { IsInWorldValue = false } };

        harness.Controller.Toggle();

        Assert.Equal(0, harness.Equipment.QueryCount);
        Assert.Empty(harness.Calls);
        Assert.Equal(CombatMode.NonCombat, harness.Combat.CurrentMode);
    }

    [Fact]
    public void PeaceWithBow_PreservesIntentSendLocalStateFeedbackOrder()
    {
        var harness = new Harness();
        harness.Equipment.Items.Add(new ClientObject
        {
            ObjectId = 1,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
            Type = ItemType.MissileWeapon,
            CombatUse = 2,
        });
        harness.Combat.CombatModeChanged += mode =>
            harness.Calls.Add($"state:{mode}");

        harness.Controller.Toggle();

        Assert.Equal(1, harness.Equipment.QueryCount);
        Assert.Equal(CombatMode.Missile, harness.Combat.CurrentMode);
        Assert.Equal(
            [
                "intent",
                "send:Missile",
                "state:Missile",
                "log:combat: Combat mode Missile",
                "toast:Combat mode Missile",
            ],
            harness.Calls);
    }

    [Fact]
    public void ActiveCombat_TogglesToPeaceRegardlessOfEquipmentDefault()
    {
        var harness = new Harness();
        harness.Combat.SetCombatMode(CombatMode.Magic);

        harness.Controller.Toggle();

        Assert.Equal(0, harness.Equipment.QueryCount);
        Assert.Equal([CombatMode.NonCombat], harness.Authority.Requests);
        Assert.Equal(CombatMode.NonCombat, harness.Combat.CurrentMode);
    }

    [Fact]
    public void PeaceWithIncompatibleHeldItem_PrintsRetailNoticeWithoutSending()
    {
        var harness = new Harness();
        harness.Equipment.Items.Add(new ClientObject
        {
            ObjectId = 1,
            Name = "Torch",
            CurrentlyEquippedLocation = EquipMask.Held,
            Type = ItemType.Misc,
        });

        harness.Controller.Toggle();

        Assert.Empty(harness.Authority.Requests);
        Assert.Equal(CombatMode.NonCombat, harness.Combat.CurrentMode);
        Assert.Equal(
            [
                "intent",
                "log:combat: You can't enter combat mode while wielding the Torch",
                "system:You can't enter combat mode while wielding the Torch",
            ],
            harness.Calls);
    }

    [Fact]
    public void TransportFailure_LeavesLocalModeAndFeedbackUnpublished()
    {
        var harness = new Harness { Authority = { ThrowOnSend = true } };

        Assert.Throws<InvalidOperationException>(harness.Controller.Toggle);

        Assert.Equal(["intent", "send:Melee"], harness.Calls);
        Assert.Equal(CombatMode.NonCombat, harness.Combat.CurrentMode);
    }

    [Fact]
    public void DeferredSlot_IsNoOpBeforeBindAndForwardsOneOwnerAfterBind()
    {
        var slot = new LiveCombatModeCommandSlot();
        var target = new FakeCommand();

        slot.Toggle();
        slot.Bind(target);
        slot.Toggle();
        slot.Bind(target);

        Assert.Equal(1, target.ToggleCount);
        Assert.Throws<InvalidOperationException>(
            () => slot.Bind(new FakeCommand()));
        slot.Unbind(target);
        slot.Toggle();
        Assert.Equal(1, target.ToggleCount);

        slot.Bind(target);
        Action copied = slot.Toggle;
        slot.Deactivate();
        copied();
        Assert.Equal(1, target.ToggleCount);
        Assert.Throws<ObjectDisposedException>(() => slot.Bind(target));
    }

    [Fact]
    public void DeferredSlotOwnedBindingReleasesExactlyAndAllowsRebind()
    {
        var slot = new LiveCombatModeCommandSlot();
        var first = new FakeCommand();
        var second = new FakeCommand();

        IDisposable firstBinding = slot.BindOwned(first);
        Assert.Throws<InvalidOperationException>(() => slot.BindOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = slot.BindOwned(second);
        firstBinding.Dispose();
        slot.Toggle();

        Assert.Equal(0, first.ToggleCount);
        Assert.Equal(1, second.ToggleCount);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Authority = new FakeAuthority(Calls);
            Equipment = new FakeEquipment();
            Intent = new FakeIntent(Calls);
            var operations = new LiveCombatModeOperations(
                Authority,
                Equipment,
                Intent);
            Runtime = new RuntimeCombatModeState(Combat, operations);
            Controller = new RuntimeCombatModeCommandAdapter(
                Runtime,
                line => Calls.Add($"log:{line}"),
                line => Calls.Add($"toast:{line}"),
                line => Calls.Add($"system:{line}"));
        }

        public List<string> Calls { get; } = [];
        public FakeAuthority Authority { get; }
        public FakeEquipment Equipment { get; }
        public CombatState Combat { get; } = new();
        public FakeIntent Intent { get; }
        public RuntimeCombatModeState Runtime { get; }
        public RuntimeCombatModeCommandAdapter Controller { get; }
    }

    private sealed class FakeAuthority(List<string> calls)
        : ILiveCombatModeAuthority
    {
        public bool IsInWorldValue { get; set; } = true;
        public bool IsInWorld => IsInWorldValue;
        public bool ThrowOnSend { get; set; }
        public List<CombatMode> Requests { get; } = [];

        public void SendChangeCombatMode(CombatMode mode)
        {
            calls.Add($"send:{mode}");
            if (ThrowOnSend)
                throw new InvalidOperationException("send");
            Requests.Add(mode);
        }
    }

    private sealed class FakeEquipment : ICombatEquipmentSource
    {
        public List<ClientObject> Items { get; } = [];
        public int QueryCount { get; private set; }

        public IReadOnlyList<ClientObject> GetOrderedEquipment()
        {
            QueryCount++;
            return Items;
        }
    }

    private sealed class FakeIntent(List<string> calls)
        : IExplicitCombatModeIntentSink
    {
        public void NotifyExplicitCombatModeRequest() => calls.Add("intent");
    }

    private sealed class FakeCommand : ILiveCombatModeCommand
    {
        public int ToggleCount { get; private set; }
        public void Toggle() => ToggleCount++;
    }
}
