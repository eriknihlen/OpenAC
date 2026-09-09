using AcDream.Core.Combat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime;

public readonly record struct RuntimeCombatAttackSnapshot(
    long Revision,
    AttackHeight RequestedHeight,
    float DesiredPower,
    float PowerBarLevel,
    bool BuildInProgress,
    bool RequestInProgress,
    float RequestedPower,
    bool RepeatAttackInProgress = false,
    bool ServerResponsePending = false)
{
    public long CompletionRevision { get; init; }
    public uint CompletionSequence { get; init; }
    public uint CompletionWeenieError { get; init; }
}

public readonly record struct RuntimeSpellCastSnapshot(
    long Revision,
    uint LastRequestedSpellId,
    uint LastRequestedTargetId);

public readonly record struct RuntimeActionSnapshot(
    long SelectionRevision,
    uint SelectedObjectId,
    uint PreviousObjectId,
    uint PreviousValidObjectId,
    long CombatRevision,
    CombatMode CombatMode,
    int TrackedTargetHealthCount,
    long InteractionRevision,
    InteractionModeKind InteractionMode,
    uint InteractionSourceObjectId,
    RuntimeInteractionTransactionSnapshot InteractionTransactions,
    RuntimeCombatAttackSnapshot CombatAttack = default,
    RuntimeSpellCastSnapshot Magic = default);

public interface IRuntimeActionView
{
    RuntimeActionSnapshot Snapshot { get; }

    bool TryGetHealth(uint objectId, out float healthPercent);
}
