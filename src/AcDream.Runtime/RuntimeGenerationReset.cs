using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.World;

namespace AcDream.Runtime;

public interface IRuntimeGenerationResetHost
{
    void RetireEntityProjection(RuntimeEntityRecord entity);

    void DrainEntityProjectionBoundary();

    void CompleteEntityProjectionRetirement();
}

public enum RuntimeGenerationResetStage
{
    None = 0,
    Transit = 1,
    CommandTargets = 2,
    ExternalContainer = 3,
    Actions = 4,
    Movement = 5,
    ObjectTable = 6,
    Character = 7,
    ItemMana = 8,
    Friends = 9,
    Squelch = 10,
    NegotiatedChannels = 11,
    Fellowship = 12,
    Allegiance = 13,
    Trade = 14,
    House = 15,
    Contracts = 16,
    Journal = 17,
    BeginEntityRetirement = 18,
    RetireEntities = 19,
    DrainHostProjection = 20,
    CompleteCanonicalEntities = 21,
    CompleteHostProjection = 22,
    ChatIdentity = 23,
    PlayerSnapshots = 24,
    PlayerIdentity = 25,
    Complete = 26,
}

public readonly record struct RuntimeGenerationResetSnapshot(
    bool IsActive,
    bool IsExecuting,
    RuntimeGenerationToken RetiringGeneration,
    RuntimeGenerationToken LastCompletedGeneration,
    RuntimeGenerationResetStage Stage,
    int RetirementCount,
    int RetirementCursor,
    bool CurrentProjectionAcknowledged,
    long TransactionId)
{
    public bool IsConverged =>
        !IsActive
        && !IsExecuting;
}

public sealed class RuntimeGenerationResetStageException(
    RuntimeGenerationToken retiringGeneration,
    RuntimeGenerationResetStage stage,
    Exception innerException) : Exception(
        $"Runtime generation {retiringGeneration.Value} reset stage "
        + $"'{stage}' did not converge.",
        innerException)
{
    public RuntimeGenerationToken RetiringGeneration { get; } =
        retiringGeneration;

    public RuntimeGenerationResetStage Stage { get; } = stage;
}

public sealed class RuntimeGenerationReset
{
    private readonly RuntimeWorldTransitState _transit;
    private readonly RuntimeCommunicationState _communication;
    private readonly RuntimeInventoryState _inventory;
    private readonly RuntimeActionState _actions;
    private readonly RuntimeLocalPlayerMovementState _movement;
    private readonly RuntimeEntityObjectLifetime _entityObjects;
    private readonly RuntimeCharacterState _character;
    private readonly RuntimeLocalPlayerIdentityState _identity;
    private readonly RuntimeFellowshipState _fellowship;
    private readonly RuntimeAllegianceState _allegiance;
    private readonly RuntimeTradeState _trade;
    private readonly RuntimeContractState _contracts;
    private readonly RuntimeJournalState _journal;
    private readonly RuntimeHouseState _house;
    private ResetState? _state;
    private RuntimeGenerationToken _lastCompletedGeneration;
    private bool _hasCompletedGeneration;
    private bool _executing;
    private long _nextTransactionId;

    internal RuntimeGenerationReset(
        RuntimeWorldTransitState transit,
        RuntimeCommunicationState communication,
        RuntimeInventoryState inventory,
        RuntimeActionState actions,
        RuntimeLocalPlayerMovementState movement,
        RuntimeEntityObjectLifetime entityObjects,
        RuntimeCharacterState character,
        RuntimeLocalPlayerIdentityState identity,
        RuntimeFellowshipState fellowship,
        RuntimeAllegianceState allegiance,
        RuntimeTradeState trade,
        RuntimeHouseState house,
        RuntimeContractState contracts,
        RuntimeJournalState journal)
    {
        _transit = transit ?? throw new ArgumentNullException(nameof(transit));
        _communication = communication
            ?? throw new ArgumentNullException(nameof(communication));
        _inventory = inventory
            ?? throw new ArgumentNullException(nameof(inventory));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _movement = movement
            ?? throw new ArgumentNullException(nameof(movement));
        _entityObjects = entityObjects
            ?? throw new ArgumentNullException(nameof(entityObjects));
        _character = character
            ?? throw new ArgumentNullException(nameof(character));
        _identity = identity
            ?? throw new ArgumentNullException(nameof(identity));
        _fellowship = fellowship
            ?? throw new ArgumentNullException(nameof(fellowship));
        _allegiance = allegiance
            ?? throw new ArgumentNullException(nameof(allegiance));
        _trade = trade ?? throw new ArgumentNullException(nameof(trade));
        _house = house ?? throw new ArgumentNullException(nameof(house));
        _contracts = contracts
            ?? throw new ArgumentNullException(nameof(contracts));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public RuntimeGenerationToken? ActiveRetiringGeneration =>
        _state?.Generation;

    public RuntimeGenerationResetSnapshot CaptureSnapshot()
    {
        ResetState? state = _state;
        return state is null
            ? new RuntimeGenerationResetSnapshot(
                false,
                _executing,
                default,
                _lastCompletedGeneration,
                _hasCompletedGeneration
                    ? RuntimeGenerationResetStage.Complete
                    : RuntimeGenerationResetStage.None,
                0,
                0,
                false,
                0)
            : new RuntimeGenerationResetSnapshot(
                true,
                _executing,
                state.Generation,
                _lastCompletedGeneration,
                state.Stage,
                state.Retirements?.Length ?? 0,
                state.RetirementCursor,
                state.CurrentProjectionAcknowledged,
                state.TransactionId);
    }

    public void Reset(
        RuntimeGenerationToken retiringGeneration,
        IRuntimeGenerationResetHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_executing)
        {
            throw new InvalidOperationException(
                "Runtime generation reset cannot run concurrently or reentrantly.");
        }

        ResetState state = AcquireState(retiringGeneration, host);
        if (state.Stage is RuntimeGenerationResetStage.Complete)
            return;

        _executing = true;
        try
        {
            Drain(state);
        }
        catch (Exception error)
        {
            throw new RuntimeGenerationResetStageException(
                state.Generation,
                state.Stage,
                error);
        }
        finally
        {
            _executing = false;
        }
    }

    internal void DrainPending()
    {
        ResetState? state = _state;
        if (state is null)
            return;
        Reset(state.Generation, state.Host);
    }

    private ResetState AcquireState(
        RuntimeGenerationToken generation,
        IRuntimeGenerationResetHost host)
    {
        if (_state is { } pending)
        {
            if (pending.Generation != generation)
            {
                throw new InvalidOperationException(
                    $"Runtime generation {pending.Generation.Value} reset "
                    + $"must converge before generation {generation.Value} can reset.");
            }
            if (!ReferenceEquals(pending.Host, host))
            {
                throw new InvalidOperationException(
                    "An in-progress Runtime generation reset cannot replace "
                    + "its borrowed projection host.");
            }
            return pending;
        }

        if (_hasCompletedGeneration)
        {
            if (generation == _lastCompletedGeneration)
                return ResetState.Completed(generation, host);
            if (generation.Value < _lastCompletedGeneration.Value)
            {
                throw new InvalidOperationException(
                    $"Runtime generation {generation.Value} reset is stale; "
                    + $"generation {_lastCompletedGeneration.Value} already converged.");
            }
        }

        var created = new ResetState(
            generation,
            host,
            checked(++_nextTransactionId));
        _state = created;
        return created;
    }

    private void Drain(ResetState state)
    {
        while (state.Stage is not RuntimeGenerationResetStage.Complete)
        {
            switch (state.Stage)
            {
                case RuntimeGenerationResetStage.Transit:
                    Advance(state, _transit.ResetSession);
                    break;
                case RuntimeGenerationResetStage.CommandTargets:
                    Advance(state, _communication.ResetCommandTargets);
                    break;
                case RuntimeGenerationResetStage.ExternalContainer:
                    Advance(state, () =>
                    {
                        _inventory.ResetExternalContainer();
                        _inventory.ResetVendor();
                    });
                    break;
                case RuntimeGenerationResetStage.Actions:
                    Advance(state, _actions.ResetSession);
                    break;
                case RuntimeGenerationResetStage.Movement:
                    Advance(state, _movement.ResetSession);
                    break;
                case RuntimeGenerationResetStage.ObjectTable:
                    Advance(state, _entityObjects.ClearObjects);
                    break;
                case RuntimeGenerationResetStage.Character:
                    Advance(state, _character.ResetSession);
                    break;
                case RuntimeGenerationResetStage.ItemMana:
                    Advance(state, _inventory.ResetItemMana);
                    break;
                case RuntimeGenerationResetStage.Friends:
                    Advance(state, _communication.ResetFriends);
                    break;
                case RuntimeGenerationResetStage.Squelch:
                    Advance(state, _communication.ResetSquelch);
                    break;
                case RuntimeGenerationResetStage.NegotiatedChannels:
                    Advance(
                        state,
                        _communication.ResetNegotiatedChannels);
                    break;
                case RuntimeGenerationResetStage.Fellowship:
                    Advance(state, _fellowship.ResetSession);
                    break;
                case RuntimeGenerationResetStage.Allegiance:
                    Advance(state, _allegiance.ResetSession);
                    break;
                case RuntimeGenerationResetStage.Trade:
                    Advance(state, _trade.Clear);
                    break;
                case RuntimeGenerationResetStage.House:
                    Advance(state, _house.ResetSession);
                    break;
                case RuntimeGenerationResetStage.Contracts:
                    Advance(state, _contracts.ResetSession);
                    break;
                case RuntimeGenerationResetStage.Journal:
                    Advance(state, _journal.ResetSession);
                    break;
                case RuntimeGenerationResetStage.BeginEntityRetirement:
                    _ = _entityObjects.BeginSessionClear();
                    state.Retirements = _entityObjects
                        .CaptureSessionClearRetirements()
                        .ToArray();
                    state.Stage = RuntimeGenerationResetStage.RetireEntities;
                    break;
                case RuntimeGenerationResetStage.RetireEntities:
                    RetireCurrentEntity(state);
                    break;
                case RuntimeGenerationResetStage.DrainHostProjection:
                    state.Host.DrainEntityProjectionBoundary();
                    state.Stage =
                        RuntimeGenerationResetStage.CompleteCanonicalEntities;
                    break;
                case RuntimeGenerationResetStage.CompleteCanonicalEntities:
                    if (!_entityObjects.CompleteSessionClearIfConverged())
                    {
                        throw new InvalidOperationException(
                            "Canonical entity/object lifetime still owns an "
                            + "unacknowledged session retirement.");
                    }
                    state.Stage =
                        RuntimeGenerationResetStage.CompleteHostProjection;
                    break;
                case RuntimeGenerationResetStage.CompleteHostProjection:
                    state.Host.CompleteEntityProjectionRetirement();
                    state.Stage = RuntimeGenerationResetStage.ChatIdentity;
                    break;
                case RuntimeGenerationResetStage.ChatIdentity:
                    Advance(state, () =>
                    {
                        _communication.ResetChatIdentity();
                        _communication.ResetSpewBox();
                    });
                    break;
                case RuntimeGenerationResetStage.PlayerSnapshots:
                    Advance(state, _inventory.ResetPlayerSnapshots);
                    break;
                case RuntimeGenerationResetStage.PlayerIdentity:
                    _identity.ResetSession();
                    Complete(state);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported Runtime generation reset stage {state.Stage}.");
            }
        }
    }

    private void RetireCurrentEntity(ResetState state)
    {
        RuntimeEntityRecord[] retirements = state.Retirements ?? [];
        if (state.RetirementCursor >= retirements.Length)
        {
            state.Stage = RuntimeGenerationResetStage.DrainHostProjection;
            return;
        }

        RuntimeEntityRecord current = retirements[state.RetirementCursor];
        if (!state.CurrentProjectionAcknowledged)
        {
            state.Host.RetireEntityProjection(current);
            state.CurrentProjectionAcknowledged = true;
        }

        _entityObjects.CompleteSessionEntityRetirement(current);
        state.CurrentProjectionAcknowledged = false;
        state.RetirementCursor++;
    }

    private static void Advance(ResetState state, Action action)
    {
        action();
        state.Stage++;
    }

    private void Complete(ResetState state)
    {
        state.Stage = RuntimeGenerationResetStage.Complete;
        _lastCompletedGeneration = state.Generation;
        _hasCompletedGeneration = true;
        _state = null;
    }

    private sealed class ResetState
    {
        public ResetState(
            RuntimeGenerationToken generation,
            IRuntimeGenerationResetHost host,
            long transactionId)
        {
            Generation = generation;
            Host = host;
            TransactionId = transactionId;
            Stage = RuntimeGenerationResetStage.Transit;
        }

        public RuntimeGenerationToken Generation { get; }
        public IRuntimeGenerationResetHost Host { get; }
        public long TransactionId { get; }
        public RuntimeGenerationResetStage Stage { get; set; }
        public RuntimeEntityRecord[]? Retirements { get; set; }
        public int RetirementCursor { get; set; }
        public bool CurrentProjectionAcknowledged { get; set; }

        public static ResetState Completed(
            RuntimeGenerationToken generation,
            IRuntimeGenerationResetHost host) =>
            new(generation, host, 0)
            {
                Stage = RuntimeGenerationResetStage.Complete,
            };
    }
}
