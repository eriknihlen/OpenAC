using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.App.Tests;

internal static class GameRuntimeTestFactory
{
    public static GameRuntime Create(
        IRuntimeCombatAttackOperations? combatAttack = null,
        IRuntimeCombatTargetOperations? combatTarget = null,
        IRuntimeCombatModeOperations? combatMode = null,
        IRuntimeSpellCastOperations? spellCast = null,
        ILiveSessionOperations? session = null,
        Func<double>? combatTime = null)
    {
        var fallback = new NoopOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            combatAttack ?? fallback,
            combatTarget ?? fallback,
            combatMode ?? fallback,
            spellCast ?? fallback,
            SessionOperations: session,
            CombatTime: combatTime));
    }

    private sealed class NoopOperations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
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
