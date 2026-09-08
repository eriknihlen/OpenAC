using AcDream.Core.World;

namespace AcDream.App.Streaming;

[Flags]
public enum LandblockRetirementStage : ushort
{
    None = 0,
    MeshReferences = 1 << 0,
    StaticScripts = 1 << 1,
    Classification = 1 << 2,
    EntityLighting = 1 << 3,
    EntityTranslucency = 1 << 4,
    PluginProjection = 1 << 5,
    Physics = 1 << 6,
    Terrain = 1 << 7,
    CellVisibility = 1 << 8,
    BuildingRegistry = 1 << 9,
    EnvironmentCells = 1 << 10,
    LegacyPresentation = 1 << 11,
}

internal enum LandblockRetirementOperationResult : byte
{
    NoWork,
    Progressed,
    Pending,
    Failed,
}

public sealed class LandblockRetirementTicket
{
    private readonly Dictionary<LandblockRetirementStage, int> _entityCursors = new();
    private readonly Dictionary<LandblockRetirementStage, Exception> _failures = new();
    private Exception? _callbackFailure;
    private Exception? _lastReportedFailure;

    internal LandblockRetirementTicket(
        GpuLandblockRetirement state,
        LandblockRetirementStage requiredStages)
    {
        State = state;
        RequiredStages = requiredStages;
    }

    public GpuLandblockRetirement State { get; }
    public uint LandblockId => State.LandblockId;
    public LandblockRetirementKind Kind => State.Kind;
    public IReadOnlyList<WorldEntity> Entities => State.Entities;
    public LandblockRetirementStage RequiredStages { get; }
    public LandblockRetirementStage CompletedStages { get; private set; }
    public bool IsComplete =>
        (CompletedStages & RequiredStages) == RequiredStages
        && _callbackFailure is null;

    public bool RunOnce(LandblockRetirementStage stage, Action operation)
    {
        ValidateSingleStage(stage);
        ArgumentNullException.ThrowIfNull(operation);
        if ((CompletedStages & stage) != 0)
            return true;

        try
        {
            operation();
            CompletedStages |= stage;
            _failures.Remove(stage);
            return true;
        }
        catch (Exception error)
        {
            _failures[stage] = error;
            return false;
        }
    }

    public bool RunOnce(LandblockRetirementStage stage, Func<bool> operation)
    {
        ValidateSingleStage(stage);
        ArgumentNullException.ThrowIfNull(operation);
        if ((CompletedStages & stage) != 0)
            return true;

        try
        {
            if (!operation())
                return false;
            CompletedStages |= stage;
            _failures.Remove(stage);
            return true;
        }
        catch (Exception error)
        {
            _failures[stage] = error;
            return false;
        }
    }

    internal LandblockRetirementOperationResult RunOnceStep(
        LandblockRetirementStage stage,
        Action operation)
    {
        ValidateSingleStage(stage);
        ArgumentNullException.ThrowIfNull(operation);
        if ((CompletedStages & stage) != 0)
            return LandblockRetirementOperationResult.NoWork;

        try
        {
            operation();
            CompletedStages |= stage;
            _failures.Remove(stage);
            return LandblockRetirementOperationResult.Progressed;
        }
        catch (Exception error)
        {
            _failures[stage] = error;
            return LandblockRetirementOperationResult.Failed;
        }
    }

    internal LandblockRetirementOperationResult RunOnceStep(
        LandblockRetirementStage stage,
        Func<bool> operation)
    {
        ValidateSingleStage(stage);
        ArgumentNullException.ThrowIfNull(operation);
        if ((CompletedStages & stage) != 0)
            return LandblockRetirementOperationResult.NoWork;

        try
        {
            if (!operation())
                return LandblockRetirementOperationResult.Pending;

            CompletedStages |= stage;
            _failures.Remove(stage);
            return LandblockRetirementOperationResult.Progressed;
        }
        catch (Exception error)
        {
            _failures[stage] = error;
            return LandblockRetirementOperationResult.Failed;
        }
    }

    public bool RunForEachEntity(
        LandblockRetirementStage stage,
        Func<WorldEntity, bool> predicate,
        Action<WorldEntity> operation)
    {
        ValidateSingleStage(stage);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(operation);
        if ((CompletedStages & stage) != 0)
            return true;

        _entityCursors.TryGetValue(stage, out int cursor);
        while (cursor < Entities.Count)
        {
            WorldEntity entity = Entities[cursor];
            if (predicate(entity))
            {
                try
                {
                    operation(entity);
                }
                catch (Exception error)
                {
                    _entityCursors[stage] = cursor;
                    _failures[stage] = error;
                    return false;
                }
            }

            cursor++;
            _entityCursors[stage] = cursor;
        }

        CompletedStages |= stage;
        _entityCursors.Remove(stage);
        _failures.Remove(stage);
        return true;
    }

    internal LandblockRetirementOperationResult RunEntityStep(
        LandblockRetirementStage stage,
        Func<WorldEntity, bool> predicate,
        Action<WorldEntity> operation)
    {
        ValidateSingleStage(stage);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(operation);
        if ((CompletedStages & stage) != 0)
            return LandblockRetirementOperationResult.NoWork;

        _entityCursors.TryGetValue(stage, out int cursor);
        if (cursor >= Entities.Count)
        {
            CompletedStages |= stage;
            _entityCursors.Remove(stage);
            _failures.Remove(stage);
            return LandblockRetirementOperationResult.Progressed;
        }

        WorldEntity entity = Entities[cursor];
        if (predicate(entity))
        {
            try
            {
                operation(entity);
            }
            catch (Exception error)
            {
                _entityCursors[stage] = cursor;
                _failures[stage] = error;
                return LandblockRetirementOperationResult.Failed;
            }
        }

        cursor++;
        if (cursor == Entities.Count)
        {
            CompletedStages |= stage;
            _entityCursors.Remove(stage);
            _failures.Remove(stage);
        }
        else
        {
            _entityCursors[stage] = cursor;
        }

        return LandblockRetirementOperationResult.Progressed;
    }

    internal LandblockRetirementStage NextIncompleteStage
    {
        get
        {
            ushort remaining = (ushort)(RequiredStages & ~CompletedStages);
            if (remaining == 0)
                return LandblockRetirementStage.None;

            ushort lowest = (ushort)(remaining & (ushort)-(short)remaining);
            return (LandblockRetirementStage)lowest;
        }
    }

    internal void BeginAttempt() => _callbackFailure = null;

    internal void RecordCallbackFailure(Exception error) =>
        _callbackFailure = error;

    internal bool TryTakeNewFailure(out Exception? error)
    {
        error = _callbackFailure ?? _failures.Values.FirstOrDefault();
        if (error is null || ReferenceEquals(error, _lastReportedFailure))
            return false;

        _lastReportedFailure = error;
        return true;
    }

    private static void ValidateSingleStage(LandblockRetirementStage stage)
    {
        ushort value = (ushort)stage;
        if (value == 0 || (value & (value - 1)) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "A retirement operation must own exactly one stage bit.");
    }
}

public sealed class LandblockRetirementCoordinator
{
    private enum BudgetedAdvanceResult : byte
    {
        Progressed,
        Yielded,
        Failed,
    }

    private enum RequestKind : byte
    {
        BeginFull,
        BeginNearLayer,
        Advance,
    }

    private readonly record struct Request(RequestKind Kind, uint LandblockId);

    private const LandblockRetirementStage CoreStages =
        LandblockRetirementStage.MeshReferences
        | LandblockRetirementStage.StaticScripts
        | LandblockRetirementStage.Classification;

    public const LandblockRetirementStage ProductionPresentationStages =
        LandblockRetirementStage.EntityLighting
        | LandblockRetirementStage.EntityTranslucency
        | LandblockRetirementStage.PluginProjection
        | LandblockRetirementStage.Terrain
        | LandblockRetirementStage.Physics
        | LandblockRetirementStage.CellVisibility
        | LandblockRetirementStage.BuildingRegistry
        | LandblockRetirementStage.EnvironmentCells;

    private readonly GpuWorldState _state;
    private readonly Action<LandblockRetirementTicket> _advancePresentation;
    private readonly Func<
        LandblockRetirementTicket,
        LandblockRetirementOperationResult>? _advancePresentationStep;
    private readonly Func<LandblockRetirementKind, LandblockRetirementStage>
        _requiredPresentationStages;
    private readonly Action<GpuLandblockRetirement>? _onDetached;
    private readonly Dictionary<uint, List<LandblockRetirementTicket>> _pending = new();
    private readonly LinkedList<LandblockRetirementTicket> _pendingOrder = new();
    private readonly Dictionary<
        LandblockRetirementTicket,
        LinkedListNode<LandblockRetirementTicket>> _pendingNodes =
            new(ReferenceEqualityComparer.Instance);
    private readonly List<uint> _completedIds = new();
    private readonly Queue<Request> _requests = new();
    private readonly HashSet<Request> _seenDuringDrain = new();
    private bool _drainingRequests;

    public LandblockRetirementCoordinator(
        GpuWorldState state,
        Action<LandblockRetirementTicket> advancePresentation,
        Func<LandblockRetirementKind, LandblockRetirementStage>? requiredPresentationStages = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(advancePresentation);
        _state = state;
        _advancePresentation = advancePresentation;
        _advancePresentationStep = null;
        _onDetached = null;
        _requiredPresentationStages = requiredPresentationStages
            ?? (kind => kind == LandblockRetirementKind.Full
                ? ProductionPresentationStages
                : ProductionPresentationStages & ~LandblockRetirementStage.Terrain);
    }

    private LandblockRetirementCoordinator(
        GpuWorldState state,
        Func<LandblockRetirementTicket, LandblockRetirementOperationResult>
            advancePresentationStep,
        Action<LandblockRetirementTicket> advancePresentation,
        Func<LandblockRetirementKind, LandblockRetirementStage>
            requiredPresentationStages,
        Action<GpuLandblockRetirement>? onDetached)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _advancePresentationStep = advancePresentationStep
            ?? throw new ArgumentNullException(nameof(advancePresentationStep));
        _advancePresentation = advancePresentation
            ?? throw new ArgumentNullException(nameof(advancePresentation));
        _requiredPresentationStages = requiredPresentationStages
            ?? throw new ArgumentNullException(nameof(requiredPresentationStages));
        _onDetached = onDetached;
    }

    public int PendingCount { get; private set; }

    public bool IsPending(uint landblockId) =>
        _pending.ContainsKey(Canonicalize(landblockId));

    internal bool MatchesState(GpuWorldState state) =>
        ReferenceEquals(_state, state);
    internal bool UsesBudgetedSteps => _advancePresentationStep is not null;

    internal Exception? AdoptDetachedFull(
        IReadOnlyList<GpuLandblockRetirement> retirements)
    {
        ArgumentNullException.ThrowIfNull(retirements);
        var incomingIds = new HashSet<uint>();
        for (int index = 0; index < retirements.Count; index++)
        {
            GpuLandblockRetirement retirement = retirements[index];
            if (retirement.Kind != LandblockRetirementKind.Full)
            {
                throw new ArgumentException(
                    "Origin-recenter adoption accepts only full retirements.",
                    nameof(retirements));
            }

            uint canonical = Canonicalize(retirement.LandblockId);
            if (!incomingIds.Add(canonical))
            {
                throw new ArgumentException(
                    $"Origin-recenter receipts contain duplicate landblock " +
                    $"0x{canonical:X8}.",
                    nameof(retirements));
            }
            if (_pending.TryGetValue(
                    canonical,
                    out List<LandblockRetirementTicket>? existing)
                && existing.Any(
                    static ticket =>
                        ticket.Kind == LandblockRetirementKind.Full))
            {
                throw new InvalidOperationException(
                    $"Landblock 0x{canonical:X8} already has a full " +
                    "retirement receipt.");
            }
        }

        LandblockRetirementStage required =
            CoreStages
            | _requiredPresentationStages(LandblockRetirementKind.Full);
        List<Exception>? failures = null;
        for (int index = 0; index < retirements.Count; index++)
        {
            GpuLandblockRetirement retirement = retirements[index];
            uint canonical = Canonicalize(retirement.LandblockId);
            _pending.TryGetValue(
                canonical,
                out List<LandblockRetirementTicket>? existing);

            var ticket = new LandblockRetirementTicket(
                retirement,
                required);
            if (existing is null)
            {
                existing = new List<LandblockRetirementTicket>(1);
                _pending.Add(canonical, existing);
            }
            existing.Add(ticket);
            PendingCount++;
            if (_advancePresentationStep is not null)
                EnqueueBudgetedTicket(ticket);

            try
            {
                _onDetached?.Invoke(retirement);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }

            if (_advancePresentationStep is null)
            {
                AdvanceTicket(ticket);
                if (ticket.IsComplete)
                {
                    existing.Remove(ticket);
                    PendingCount--;
                    if (existing.Count == 0)
                        _pending.Remove(canonical);
                }
            }
        }

        return failures switch
        {
            null => null,
            { Count: 1 } => failures[0],
            _ => new AggregateException(
                "One or more detached landblock observers failed.",
                failures),
        };
    }

    public void BeginFull(uint landblockId) => EnqueueRequest(
        new Request(RequestKind.BeginFull, Canonicalize(landblockId)));

    public void BeginNearLayer(uint landblockId) => EnqueueRequest(
        new Request(RequestKind.BeginNearLayer, Canonicalize(landblockId)));

    public void Advance() => EnqueueRequest(
        new Request(RequestKind.Advance, 0u));

    internal static LandblockRetirementCoordinator CreateBudgeted(
        GpuWorldState state,
        Func<LandblockRetirementTicket, LandblockRetirementOperationResult>
            advancePresentationStep,
        Action<LandblockRetirementTicket> advancePresentation,
        Action<GpuLandblockRetirement>? onDetached = null)
    {
        return new LandblockRetirementCoordinator(
            state,
            advancePresentationStep,
            advancePresentation,
            kind => kind == LandblockRetirementKind.Full
                ? ProductionPresentationStages
                : ProductionPresentationStages & ~LandblockRetirementStage.Terrain,
            onDetached);
    }

    public void Advance(StreamingWorkMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        if (_advancePresentationStep is null)
        {
            Advance();
            return;
        }

        while (_pendingOrder.First is { } head)
        {
            LandblockRetirementTicket ticket = head.Value;
            if (ticket.IsComplete)
            {
                RemoveCompletedTicket(ticket);
                continue;
            }

            if (AdvanceBudgetedTicket(ticket, meter)
                != BudgetedAdvanceResult.Progressed)
                return;
            if (ticket.IsComplete)
                RemoveCompletedTicket(ticket);
        }
    }

    internal void AdvancePriority(uint landblockId, StreamingWorkMeter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        if (_advancePresentationStep is null)
        {
            Advance();
            return;
        }

        uint canonical = Canonicalize(landblockId);
        while (_pending.TryGetValue(
            canonical,
            out List<LandblockRetirementTicket>? tickets))
        {
            if (tickets.Count == 0)
                throw new InvalidOperationException(
                    "A pending retirement owner has no tickets.");

            LandblockRetirementTicket ticket = tickets[0];
            if (!ticket.IsComplete
                && AdvanceBudgetedTicket(ticket, meter)
                    != BudgetedAdvanceResult.Progressed)
            {
                return;
            }

            if (ticket.IsComplete)
                RemoveCompletedTicket(ticket);
        }
    }

    private void AdvanceCore()
    {
        if (_advancePresentationStep is not null)
        {
            AdvanceBudgetedEagerAttempt();
            return;
        }

        if (_pending.Count == 0)
            return;

        _completedIds.Clear();
        foreach ((uint id, List<LandblockRetirementTicket> tickets) in _pending)
        {
            for (int i = tickets.Count - 1; i >= 0; i--)
            {
                LandblockRetirementTicket ticket = tickets[i];
                AdvanceTicket(ticket);
                if (!ticket.IsComplete)
                    continue;

                tickets.RemoveAt(i);
                PendingCount--;
            }

            if (tickets.Count == 0)
                _completedIds.Add(id);
        }

        for (int i = 0; i < _completedIds.Count; i++)
            _pending.Remove(_completedIds[i]);
    }

    internal static LandblockRetirementCoordinator CreateLegacy(
        GpuWorldState state,
        Action<uint>? removeTerrain,
        Action<uint>? demoteNearLayer)
    {
        LandblockRetirementStage Required(LandblockRetirementKind kind) =>
            kind switch
            {
                LandblockRetirementKind.Full when removeTerrain is not null =>
                    LandblockRetirementStage.LegacyPresentation,
                LandblockRetirementKind.NearLayer when demoteNearLayer is not null =>
                    LandblockRetirementStage.LegacyPresentation,
                _ => LandblockRetirementStage.None,
            };

        void AdvanceLegacy(LandblockRetirementTicket ticket)
        {
            Action<uint>? callback = ticket.Kind == LandblockRetirementKind.Full
                ? removeTerrain
                : demoteNearLayer;
            if (callback is not null)
            {
                ticket.RunOnce(
                    LandblockRetirementStage.LegacyPresentation,
                    () => callback(ticket.LandblockId));
            }
        }

        return new LandblockRetirementCoordinator(state, AdvanceLegacy, Required);
    }

    private void BeginCore(uint landblockId, LandblockRetirementKind kind)
    {
        uint canonical = Canonicalize(landblockId);
        if (_pending.TryGetValue(canonical, out List<LandblockRetirementTicket>? existing))
        {
            for (int i = 0; i < existing.Count; i++)
            {
                if (existing[i].Kind == kind)
                {
                    LandblockRetirementTicket existingTicket = existing[i];
                    if (_advancePresentationStep is not null)
                        return;

                    AdvanceTicket(existingTicket);
                    if (existingTicket.IsComplete)
                    {
                        existing.RemoveAt(i);
                        PendingCount--;
                        if (existing.Count == 0)
                            _pending.Remove(canonical);
                    }
                    return;
                }
            }
        }

        LandblockRetirementStage required =
            CoreStages | _requiredPresentationStages(kind);
        GpuLandblockRetirement? stateRetirement = null;
        Exception? detachmentObserverFailure = null;
        GpuWorldState.MutationBatch mutation = _state.BeginMutationBatch();
        try
        {
            stateRetirement = kind == LandblockRetirementKind.Full
                ? _state.DetachLandblock(canonical)
                : _state.DetachNearLayer(canonical);
        }
        finally
        {
            try
            {
                mutation.Dispose();
            }
            catch (Exception error)
            {
                detachmentObserverFailure = error;
            }
        }
        if (stateRetirement is null)
        {
            if (detachmentObserverFailure is not null)
                throw detachmentObserverFailure;
            return;
        }

        var ticket = new LandblockRetirementTicket(stateRetirement, required);
        if (existing is null)
        {
            existing = new List<LandblockRetirementTicket>(1);
            _pending.Add(canonical, existing);
        }
        existing.Add(ticket);
        PendingCount++;
        if (_advancePresentationStep is not null)
            EnqueueBudgetedTicket(ticket);

        _onDetached?.Invoke(stateRetirement);

        if (_advancePresentationStep is null)
        {
            AdvanceTicket(ticket);
            if (ticket.IsComplete)
            {
                existing.Remove(ticket);
                PendingCount--;
                if (existing.Count == 0)
                    _pending.Remove(canonical);
            }
        }
        if (detachmentObserverFailure is not null)
            throw detachmentObserverFailure;
    }

    private void AdvanceTicket(LandblockRetirementTicket ticket)
    {
        ticket.BeginAttempt();
        ticket.RunOnce(
            LandblockRetirementStage.MeshReferences,
            () => _state.ReleaseLandblockMeshReferences(ticket.LandblockId));
        ticket.RunForEachEntity(
            LandblockRetirementStage.StaticScripts,
            static entity => entity.ServerGuid == 0,
            _state.StopStaticEntityScript);
        ticket.RunOnce(
            LandblockRetirementStage.Classification,
            () => _state.InvalidateLandblockClassification(ticket.LandblockId));

        try
        {
            _advancePresentation(ticket);
        }
        catch (Exception error)
        {
            ticket.RecordCallbackFailure(error);
        }

        if (ticket.TryTakeNewFailure(out Exception? failure))
        {
            Console.WriteLine(
                $"streaming: retirement for 0x{ticket.LandblockId:X8} " +
                $"({ticket.Kind}) remains pending: {failure}");
        }
    }

    private void AdvanceBudgetedEagerAttempt()
    {
        if (_pendingOrder.First is not { } head)
            return;
        LandblockRetirementTicket ticket = head.Value;

        if (ticket.IsComplete)
        {
            RemoveCompletedTicket(ticket);
            return;
        }

        AdvanceTicket(ticket);
        if (ticket.IsComplete)
            RemoveCompletedTicket(ticket);
    }

    private LandblockRetirementOperationResult AdvanceTicketOne(
        LandblockRetirementTicket ticket)
    {
        ticket.BeginAttempt();
        LandblockRetirementStage stage = ticket.NextIncompleteStage;
        LandblockRetirementOperationResult result;
        try
        {
            result = stage switch
            {
                LandblockRetirementStage.MeshReferences =>
                    ticket.RunOnceStep(
                        stage,
                        () => _state.ReleaseLandblockMeshReferences(
                            ticket.LandblockId)),
                LandblockRetirementStage.StaticScripts =>
                    ticket.RunEntityStep(
                        stage,
                        static entity => entity.ServerGuid == 0,
                        _state.StopStaticEntityScript),
                LandblockRetirementStage.Classification =>
                    ticket.RunOnceStep(
                        stage,
                        () => _state.InvalidateLandblockClassification(
                            ticket.LandblockId)),
                LandblockRetirementStage.None =>
                    LandblockRetirementOperationResult.NoWork,
                _ => _advancePresentationStep!(ticket),
            };
        }
        catch (Exception error)
        {
            ticket.RecordCallbackFailure(error);
            result = LandblockRetirementOperationResult.Failed;
        }

        if (ticket.TryTakeNewFailure(out Exception? failure))
        {
            Console.WriteLine(
                $"streaming: retirement for 0x{ticket.LandblockId:X8} " +
                $"({ticket.Kind}) remains pending: {failure}");
        }

        return result;
    }

    private BudgetedAdvanceResult AdvanceBudgetedTicket(
        LandblockRetirementTicket ticket,
        StreamingWorkMeter meter)
    {
        LandblockRetirementStage stage = ticket.NextIncompleteStage;
        if (stage == LandblockRetirementStage.None)
        {
            throw new InvalidOperationException(
                "An incomplete retirement ticket has no unfinished stage.");
        }

        StreamingWorkCost cost = new(
            EntityOperations: 1,
            GlRetireOperations:
                stage is LandblockRetirementStage.MeshReferences
                    or LandblockRetirementStage.Terrain
                    ? 1
                    : 0);
        StreamingWorkAdmission admission = meter.TryReserve(
            cost,
            $"retire-{stage}-0x{ticket.LandblockId:X8}");
        if (admission == StreamingWorkAdmission.Yielded)
            return BudgetedAdvanceResult.Yielded;

        LandblockRetirementOperationResult result = AdvanceTicketOne(ticket);
        if (result == LandblockRetirementOperationResult.Failed)
        {
            meter.Fail();
            return BudgetedAdvanceResult.Failed;
        }

        meter.Complete();
        if (result == LandblockRetirementOperationResult.Pending)
            return BudgetedAdvanceResult.Yielded;
        return BudgetedAdvanceResult.Progressed;
    }

    private void EnqueueBudgetedTicket(LandblockRetirementTicket ticket)
    {
        LinkedListNode<LandblockRetirementTicket> node =
            _pendingOrder.AddLast(ticket);
        if (!_pendingNodes.TryAdd(ticket, node))
        {
            _pendingOrder.Remove(node);
            throw new InvalidOperationException(
                "A retirement ticket cannot enter the budgeted FIFO twice.");
        }
    }

    private void RemoveCompletedTicket(LandblockRetirementTicket ticket)
    {
        if (!_pendingNodes.Remove(
                ticket,
                out LinkedListNode<LandblockRetirementTicket>? node))
        {
            throw new InvalidOperationException(
                "Completed retirement ticket is missing from its budgeted order.");
        }
        _pendingOrder.Remove(node);

        uint id = Canonicalize(ticket.LandblockId);
        if (!_pending.TryGetValue(id, out List<LandblockRetirementTicket>? tickets)
            || !tickets.Remove(ticket))
        {
            throw new InvalidOperationException(
                "Completed retirement ticket is missing from its owner ledger.");
        }

        PendingCount--;
        if (tickets.Count == 0)
            _pending.Remove(id);
    }

    private static uint Canonicalize(uint id) =>
        (id & 0xFFFF0000u) | 0xFFFFu;

    private void EnqueueRequest(Request request)
    {
        if (_drainingRequests)
        {
            if (_seenDuringDrain.Add(request))
                _requests.Enqueue(request);
            return;
        }

        _drainingRequests = true;
        _seenDuringDrain.Clear();
        _requests.Clear();
        _seenDuringDrain.Add(request);
        _requests.Enqueue(request);
        List<Exception>? failures = null;
        try
        {
            while (_requests.TryDequeue(out Request pending))
            {
                try
                {
                    switch (pending.Kind)
                    {
                        case RequestKind.BeginFull:
                            BeginCore(
                                pending.LandblockId,
                                LandblockRetirementKind.Full);
                            break;
                        case RequestKind.BeginNearLayer:
                            BeginCore(
                                pending.LandblockId,
                                LandblockRetirementKind.NearLayer);
                            break;
                        case RequestKind.Advance:
                            AdvanceCore();
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unknown retirement request {pending.Kind}.");
                    }
                }
                catch (Exception error)
                {
                    (failures ??= new List<Exception>()).Add(error);
                }
            }
        }
        finally
        {
            _requests.Clear();
            _seenDuringDrain.Clear();
            _drainingRequests = false;
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failures[0])
                .Throw();
        if (failures is { Count: > 1 })
        {
            throw new AggregateException(
                "One or more serialized landblock retirement requests failed.",
                failures);
        }
    }
}
