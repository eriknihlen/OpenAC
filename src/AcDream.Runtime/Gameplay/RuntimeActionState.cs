using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using System.Diagnostics;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeActionOwnershipSnapshot(
    bool IsDisposed,
    bool InternalSubscriptionsAttached,
    bool InteractionTransactionsDisposed,
    bool CombatAttackDisposed,
    bool CombatTargetDisposed,
    bool SpellCastReset,
    uint SelectedObjectId,
    uint PreviousObjectId,
    uint PreviousValidObjectId,
    CombatMode CombatMode,
    int TrackedTargetHealthCount,
    InteractionMode InteractionMode,
    RuntimeInteractionTransactionSnapshot InteractionTransactions,
    long SelectionRevision,
    long CombatRevision,
    long InteractionRevision)
{
    public bool IsConverged =>
        IsDisposed
        && !InternalSubscriptionsAttached
        && InteractionTransactionsDisposed
        && CombatAttackDisposed
        && CombatTargetDisposed
        && SpellCastReset
        && SelectedObjectId == 0u
        && PreviousObjectId == 0u
        && PreviousValidObjectId == 0u
        && CombatMode == CombatMode.NonCombat
        && TrackedTargetHealthCount == 0
        && InteractionMode == InteractionMode.None
        && InteractionTransactions.IsConverged;
}

public sealed class RuntimeActionState : IDisposable
{
    private bool _disposed;
    private bool _internalSubscriptionsAttached;
    private long _selectionRevision;
    private long _combatRevision;
    private long _interactionRevision;
    private long _combatIntentRevision;
    private long _magicIntentRevision;
    private readonly Func<double> _now;
    private readonly Dictionary<uint, HealthActivity> _healthActivity = [];
    private long _healthActivityRevision;

    public RuntimeActionState(
        InventoryTransactionState inventoryTransactions,
        Spellbook spellbook,
        IRuntimeCombatAttackOperations combatAttackOperations,
        IRuntimeCombatTargetOperations combatTargetOperations,
        IRuntimeCombatModeOperations combatModeOperations,
        IRuntimeSpellCastOperations spellCastOperations,
        Func<double>? now = null)
    {
        ArgumentNullException.ThrowIfNull(inventoryTransactions);
        ArgumentNullException.ThrowIfNull(spellbook);
        ArgumentNullException.ThrowIfNull(combatAttackOperations);
        ArgumentNullException.ThrowIfNull(combatTargetOperations);
        ArgumentNullException.ThrowIfNull(combatModeOperations);
        ArgumentNullException.ThrowIfNull(spellCastOperations);
        _now = now ?? (() =>
            Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        Selection = new SelectionState();
        Combat = new CombatState();
        Interaction = new InteractionState();
        Transactions = new RuntimeInteractionTransactionState(
            inventoryTransactions);
        CombatAttack = new RuntimeCombatAttackState(
            Combat,
            combatAttackOperations,
            _now);
        CombatTarget = new RuntimeCombatTargetState(
            Combat,
            Selection,
            combatTargetOperations);
        CombatMode = new RuntimeCombatModeState(
            Combat,
            combatModeOperations);
        SpellCast = new RuntimeSpellCastState(
            spellbook,
            Selection,
            spellCastOperations);
        View = new ActionView(this);

        Selection.Changed += OnSelectionChanged;
        Combat.CombatModeChanged += OnCombatModeChanged;
        Combat.HealthChanged += OnHealthChanged;
        Interaction.Changed += OnInteractionChanged;
        CombatAttack.StateChanged += OnCombatAttackChanged;
        SpellCast.StateChanged += OnSpellCastChanged;
        _internalSubscriptionsAttached = true;
    }

    public SelectionState Selection { get; }
    public CombatState Combat { get; }
    public InteractionState Interaction { get; }
    public RuntimeInteractionTransactionState Transactions { get; }
    public RuntimeCombatAttackState CombatAttack { get; }
    public RuntimeCombatTargetState CombatTarget { get; }
    public RuntimeCombatModeState CombatMode { get; }
    public RuntimeSpellCastState SpellCast { get; }
    public IRuntimeActionView View { get; }
    public bool IsDisposed => _disposed;

    public bool TryGetHealthActivity(
        uint objectId,
        out long revision,
        out double secondsSinceUpdate)
    {
        if (!_healthActivity.TryGetValue(objectId, out HealthActivity activity))
        {
            revision = 0;
            secondsSinceUpdate = double.PositiveInfinity;
            return false;
        }
        revision = activity.Revision;
        secondsSinceUpdate = Math.Max(0d, _now() - activity.UpdatedAt);
        return true;
    }

    internal event Action? CombatChanged;

    public RuntimeActionOwnershipSnapshot CaptureOwnership() => new(
        _disposed,
        _internalSubscriptionsAttached,
        Transactions.IsDisposed,
        CombatAttack.IsDisposed,
        CombatTarget.IsDisposed,
        SpellCast.LastRequestedSpellId is null
            && SpellCast.LastRequestedTargetId is null,
        Selection.SelectedObjectId ?? 0u,
        Selection.PreviousObjectId ?? 0u,
        Selection.PreviousValidObjectId ?? 0u,
        Combat.CurrentMode,
        Combat.TrackedTargetCount,
        Interaction.Current,
        Transactions.CaptureOwnership(),
        Interlocked.Read(ref _selectionRevision),
        Interlocked.Read(ref _combatRevision),
        Interlocked.Read(ref _interactionRevision));

    public void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<Exception>? failures = null;
        Try(Transactions.ResetSession, ref failures);
        Try(Interaction.ResetSession, ref failures);
        Try(SpellCast.Reset, ref failures);
        Try(CombatAttack.ResetSession, ref failures);
        Try(() => Selection.Reset(), ref failures);
        Try(Combat.Clear, ref failures);
        ClearHealthActivity();
        if (failures is not null)
        {
            throw new AggregateException(
                "Runtime action state did not converge during reset.",
                failures);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        List<Exception>? failures = null;
        try
        {
            Try(Transactions.Dispose, ref failures);
            Try(Interaction.ResetSession, ref failures);
            Try(SpellCast.Reset, ref failures);
            Try(CombatAttack.ResetSession, ref failures);
            Try(() => Selection.Reset(), ref failures);
            Try(Combat.Clear, ref failures);
            ClearHealthActivity();
        }
        finally
        {
            Selection.Changed -= OnSelectionChanged;
            Combat.CombatModeChanged -= OnCombatModeChanged;
            Combat.HealthChanged -= OnHealthChanged;
            Interaction.Changed -= OnInteractionChanged;
            CombatAttack.StateChanged -= OnCombatAttackChanged;
            SpellCast.StateChanged -= OnSpellCastChanged;
            Try(CombatAttack.Dispose, ref failures);
            Try(CombatTarget.Dispose, ref failures);
            _internalSubscriptionsAttached = false;
            _disposed = true;
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "Runtime action state did not converge during disposal.",
                failures);
        }
    }

    private void OnSelectionChanged(SelectionTransition _) =>
        Interlocked.Increment(ref _selectionRevision);

    private void OnCombatModeChanged(CombatMode _)
    {
        Interlocked.Increment(ref _combatRevision);
        CombatChanged?.Invoke();
    }

    private void OnHealthChanged(uint objectId, float _)
    {
        long revision = ++_healthActivityRevision;
        _healthActivity[objectId] = new HealthActivity(revision, _now());
        Interlocked.Increment(ref _combatRevision);
        CombatChanged?.Invoke();
    }

    private void ClearHealthActivity()
    {
        _healthActivity.Clear();
        _healthActivityRevision = 0;
    }

    private void OnInteractionChanged(InteractionModeTransition _) =>
        Interlocked.Increment(ref _interactionRevision);

    private void OnCombatAttackChanged()
    {
        Interlocked.Increment(ref _combatIntentRevision);
        CombatChanged?.Invoke();
    }

    private void OnSpellCastChanged() =>
        Interlocked.Increment(ref _magicIntentRevision);

    private static void Try(Action action, ref List<Exception>? failures)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            (failures ??= []).Add(error);
        }
    }

    private sealed class ActionView(RuntimeActionState owner)
        : IRuntimeActionView
    {
        public RuntimeActionSnapshot Snapshot => new(
            Interlocked.Read(ref owner._selectionRevision),
            owner.Selection.SelectedObjectId ?? 0u,
            owner.Selection.PreviousObjectId ?? 0u,
            owner.Selection.PreviousValidObjectId ?? 0u,
            Interlocked.Read(ref owner._combatRevision),
            owner.Combat.CurrentMode,
            owner.Combat.TrackedTargetCount,
            Interlocked.Read(ref owner._interactionRevision),
            owner.Interaction.Current.Kind,
            owner.Interaction.Current.SourceObjectId,
            owner.Transactions.CaptureOwnership(),
            new RuntimeCombatAttackSnapshot(
                Interlocked.Read(ref owner._combatIntentRevision),
                owner.CombatAttack.RequestedHeight,
                owner.CombatAttack.DesiredPower,
                owner.CombatAttack.PowerBarLevel,
                owner.CombatAttack.BuildInProgress,
                owner.CombatAttack.AttackRequestInProgress,
                owner.CombatAttack.RequestedAttackPower,
                owner.CombatAttack.RepeatAttackInProgress,
                owner.CombatAttack.AttackServerResponsePending)
            {
                CompletionRevision = owner.CombatAttack.CompletionRevision,
                CompletionSequence = owner.CombatAttack.CompletionSequence,
                CompletionWeenieError = owner.CombatAttack.CompletionWeenieError,
            },
            new RuntimeSpellCastSnapshot(
                Interlocked.Read(ref owner._magicIntentRevision),
                owner.SpellCast.LastRequestedSpellId ?? 0u,
                owner.SpellCast.LastRequestedTargetId ?? 0u));

        public bool TryGetHealth(uint objectId, out float healthPercent)
        {
            if (!owner.Combat.HasHealth(objectId))
            {
                healthPercent = 0f;
                return false;
            }

            healthPercent = owner.Combat.GetHealthPercent(objectId);
            return true;
        }
    }

    private readonly record struct HealthActivity(long Revision, double UpdatedAt);
}
