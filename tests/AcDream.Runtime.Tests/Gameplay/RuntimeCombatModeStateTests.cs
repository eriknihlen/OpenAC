using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCombatModeStateTests
{
    [Fact]
    public void OutsideWorld_IsCompleteNoOp()
    {
        var combat = new CombatState();
        var operations = new Operations { IsInWorld = false };
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Inactive, result.Status);
        Assert.Empty(operations.Trace);
        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
    }

    [Fact]
    public void PeaceWithBow_OrdersIntentSendThenLocalState()
    {
        var combat = new CombatState();
        var operations = new Operations();
        operations.Equipment.Add(new ClientObject
        {
            ObjectId = 1u,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
            Type = ItemType.MissileWeapon,
            CombatUse = 2,
        });
        combat.CombatModeChanged += mode =>
            operations.Trace.Add($"state:{mode}");
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.Missile, result.Mode);
        Assert.Equal(
            ["intent", "equipment", "send:Missile", "state:Missile"],
            operations.Trace);
    }

    [Fact]
    public void ActiveCombat_LeavesWithoutEquipmentQuery()
    {
        var combat = new CombatState();
        combat.SetCombatMode(CombatMode.Magic);
        var operations = new Operations();
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.NonCombat, result.Mode);
        Assert.DoesNotContain("equipment", operations.Trace);
    }

    [Fact]
    public void IncompatibleHeldItem_RejectsAfterExplicitIntent()
    {
        var combat = new CombatState();
        var operations = new Operations();
        operations.Equipment.Add(new ClientObject
        {
            ObjectId = 1u,
            Name = "Torch",
            CurrentlyEquippedLocation = EquipMask.Held,
            Type = ItemType.Misc,
        });
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Rejected, result.Status);
        Assert.Equal(
            "You can't enter combat mode while wielding the Torch",
            result.Notice);
        Assert.Equal(["intent", "equipment"], operations.Trace);
    }

    [Fact]
    public void TransportFailure_DoesNotPublishLocalMode()
    {
        var combat = new CombatState();
        var operations = new Operations { ThrowOnSend = true };
        var state = new RuntimeCombatModeState(combat, operations);

        Assert.Throws<InvalidOperationException>(() => state.Toggle());

        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
        Assert.Equal(["intent", "equipment", "send:Melee"], operations.Trace);
    }

    [Fact]
    public void ExplicitRequestSendsChosenPluginModeWithoutEquipmentGuessing()
    {
        var combat = new CombatState();
        var operations = new Operations();
        combat.CombatModeChanged += mode =>
            operations.Trace.Add($"state:{mode}");
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Request(CombatMode.Magic);

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.Magic, result.Mode);
        Assert.Equal(
            ["intent", "send:Magic", "state:Magic"],
            operations.Trace);
    }

    private sealed class Operations : IRuntimeCombatModeOperations
    {
        public bool IsInWorld { get; init; } = true;
        public bool ThrowOnSend { get; init; }
        public List<ClientObject> Equipment { get; } = [];
        public List<string> Trace { get; } = [];

        public IReadOnlyList<ClientObject> GetOrderedEquipment()
        {
            Trace.Add("equipment");
            return Equipment;
        }

        public void NotifyExplicitCombatModeRequest() => Trace.Add("intent");

        public void SendChangeCombatMode(CombatMode mode)
        {
            Trace.Add($"send:{mode}");
            if (ThrowOnSend)
                throw new InvalidOperationException("transport");
        }
    }
}
