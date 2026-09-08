using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

internal static class RuntimeActionTestFactory
{
    public static RuntimeActionState Create(
        InventoryTransactionState inventory,
        Func<double>? now = null) =>
        new(
            inventory,
            new Spellbook(),
            new CombatOperations(),
            new CombatTargetOperations(),
            new CombatModeOperations(),
            new SpellOperations(),
            now);

    private sealed class CombatOperations : IRuntimeCombatAttackOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
    }

    private sealed class CombatTargetOperations
        : IRuntimeCombatTargetOperations
    {
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
    }

    private sealed class CombatModeOperations
        : IRuntimeCombatModeOperations
    {
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
    }

    private sealed class SpellOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
