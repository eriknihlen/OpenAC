using System.Collections.Immutable;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Entities;

public sealed class ParentAttachmentState
{
    private readonly Dictionary<uint, Queue<ParentAttachmentRelation>> _unresolvedByChild = new();
    private readonly Dictionary<uint, ParentAttachmentRelation> _stagedByChild = new();
    private readonly Dictionary<uint, ParentAttachmentRelation> _recoveryByChild = new();
    private readonly Dictionary<uint, ParentAttachmentRelation> _lastAcceptedByChild = new();
    private readonly Dictionary<ParentIncarnation, List<uint>> _committedChildrenByParent = new();
    private readonly Dictionary<uint, Queue<DeferredParentCreate>>
        _deferredCreatesByParent = [];
    private readonly Dictionary<uint, Queue<DeferredAcceptedParentRelation>>
        _deferredAcceptedRelationsByParent = [];
    private ulong _nextDeferredCreateAdmissionId;
    private readonly HashSet<uint> _loggedIncarnationRefusals = [];

    private sealed class CreateWindowState
    {
        internal required uint ParentGuid { get; init; }
        internal List<Func<DeferredParentCreate, bool>> Filters { get; } = [];
    }

    private sealed class RelationWindowState
    {
        internal required uint ParentGuid { get; init; }
        internal List<Func<DeferredAcceptedParentRelation, bool>> Filters { get; } = [];
    }

    private readonly Dictionary<ulong, CreateWindowState> _createWindows = [];
    private readonly Dictionary<ulong, RelationWindowState> _relationWindows = [];
    private ulong _nextWindowId;

    public int UnresolvedRelationCount =>
        _unresolvedByChild.Values.Sum(queue => queue.Count);
    public int StagedRelationCount => _stagedByChild.Count;
    public int RecoveryRelationCount => _recoveryByChild.Count;
    public int CommittedRelationCount => _lastAcceptedByChild.Count;
    internal int DeferredCreateCount =>
        _deferredCreatesByParent.Values.Sum(queue => queue.Count);
    internal int DeferredAcceptedRelationCount =>
        _deferredAcceptedRelationsByParent.Values.Sum(queue => queue.Count);

    internal void EnqueueDeferredCreate(
        WorldSession.EntitySpawn spawn,
        bool isLocalPlayer)
    {
        uint parentGuid = spawn.ParentGuid
            ?? spawn.Physics?.Parent?.Guid
            ?? 0u;
        if (spawn.Guid == 0u || parentGuid == 0u)
        {
            throw new ArgumentException(
                "A deferred parent CreateObject requires nonzero child and parent GUIDs.",
                nameof(spawn));
        }
        if (_nextDeferredCreateAdmissionId == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The deferred parent CreateObject admission sequence is exhausted.");
        }
        if (!_deferredCreatesByParent.TryGetValue(
                parentGuid,
                out Queue<DeferredParentCreate>? queue))
        {
            queue = new Queue<DeferredParentCreate>();
            _deferredCreatesByParent.Add(parentGuid, queue);
        }
        ulong admissionId = _nextDeferredCreateAdmissionId + 1UL;
        queue.Enqueue(new DeferredParentCreate(
            admissionId,
            RuntimeInitialCreateAdmissionFreezer.Freeze(spawn),
            isLocalPlayer));
        _nextDeferredCreateAdmissionId = admissionId;
    }

    internal bool TryPeekDeferredCreate(
        uint parentGuid,
        out DeferredParentCreate deferred)
    {
        if (!_deferredCreatesByParent.TryGetValue(
                parentGuid,
                out Queue<DeferredParentCreate>? queue)
            || !queue.TryPeek(out deferred))
        {
            deferred = default;
            return false;
        }
        return true;
    }

    internal bool ConsumeDeferredCreate(
        uint parentGuid,
        in DeferredParentCreate expected)
    {
        if (!_deferredCreatesByParent.TryGetValue(
                parentGuid,
                out Queue<DeferredParentCreate>? queue)
            || !queue.TryPeek(out DeferredParentCreate current)
            || current != expected)
        {
            return false;
        }
        _ = queue.Dequeue();
        if (queue.Count == 0)
            _deferredCreatesByParent.Remove(parentGuid);
        return true;
    }

    internal ImmutableArray<DeferredParentCreate> DetachDeferredCreates(
        uint parentGuid,
        out DeferredReplayWindowToken window)
    {
        if (!_deferredCreatesByParent.Remove(
                parentGuid,
                out Queue<DeferredParentCreate>? queue))
        {
            window = default;
            return ImmutableArray<DeferredParentCreate>.Empty;
        }
        ulong id = ++_nextWindowId;
        _createWindows[id] = new CreateWindowState { ParentGuid = parentGuid };
        window = new DeferredReplayWindowToken(id, parentGuid, DeferredReplayBucketKind.Creates);
        return [.. queue];
    }

    internal void RestoreDeferredCreates(
        in DeferredReplayWindowToken window,
        ReadOnlySpan<DeferredParentCreate> entries)
    {
        if (window.Kind != DeferredReplayBucketKind.Creates
            || !_createWindows.Remove(window.Id, out CreateWindowState? state))
        {
            return;
        }
        if (entries.Length == 0)
            return;
        IEnumerable<DeferredParentCreate> filtered = entries.ToArray();
        foreach (Func<DeferredParentCreate, bool> filter in state.Filters)
            filtered = filtered.Where(filter);
        DeferredParentCreate[] survivors = filtered.ToArray();
        if (survivors.Length == 0)
            return;
        var restored = new Queue<DeferredParentCreate>(survivors.Length);
        foreach (DeferredParentCreate entry in survivors)
            restored.Enqueue(entry);
        if (_deferredCreatesByParent.TryGetValue(
                window.ParentGuid,
                out Queue<DeferredParentCreate>? existing))
        {
            foreach (DeferredParentCreate entry in existing)
                restored.Enqueue(entry);
        }
        _deferredCreatesByParent[window.ParentGuid] = restored;
    }

    internal bool ContainsDeferredCreate(
        uint childGuid,
        ushort instanceSequence)
    {
        foreach (Queue<DeferredParentCreate> queue
            in _deferredCreatesByParent.Values)
        {
            if (queue.Any(candidate =>
                    candidate.Spawn.Guid == childGuid
                    && candidate.Spawn.InstanceSequence == instanceSequence))
            {
                return true;
            }
        }
        return false;
    }

    internal void EnqueueDeferredAcceptedRelation(
        uint childGuid,
        RuntimeEntityKey childKey,
        ParentEvent.Parsed? standalone,
        CreateParentUpdate? envelope,
        AcceptedPhysicsTimestamps acceptedTimestamps)
    {
        uint parentGuid = standalone?.ParentGuid ?? envelope?.ParentGuid ?? 0u;
        if (parentGuid == 0u || childGuid == 0u)
        {
            throw new ArgumentException(
                "A deferred accepted parent relation requires nonzero parent and child GUIDs.");
        }
        if (_nextDeferredCreateAdmissionId == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "The deferred parent CreateObject admission sequence is exhausted.");
        }
        ulong admissionId = _nextDeferredCreateAdmissionId + 1UL;
        EnqueueDeferredAcceptedRelation(new DeferredAcceptedParentRelation(
            admissionId, childGuid, childKey, standalone, envelope, acceptedTimestamps));
        _nextDeferredCreateAdmissionId = admissionId;
    }

    internal void EnqueueDeferredAcceptedRelation(
        in DeferredAcceptedParentRelation relation)
    {
        uint parentGuid = relation.Standalone?.ParentGuid
            ?? relation.Envelope?.ParentGuid
            ?? 0u;
        if (!_deferredAcceptedRelationsByParent.TryGetValue(
                parentGuid,
                out Queue<DeferredAcceptedParentRelation>? queue))
        {
            queue = new Queue<DeferredAcceptedParentRelation>();
            _deferredAcceptedRelationsByParent.Add(parentGuid, queue);
        }
        queue.Enqueue(relation);
    }

    internal ImmutableArray<DeferredAcceptedParentRelation> DetachDeferredAcceptedRelations(
        uint parentGuid,
        out DeferredReplayWindowToken window)
    {
        if (!_deferredAcceptedRelationsByParent.Remove(
                parentGuid,
                out Queue<DeferredAcceptedParentRelation>? queue))
        {
            window = default;
            return ImmutableArray<DeferredAcceptedParentRelation>.Empty;
        }
        ulong id = ++_nextWindowId;
        _relationWindows[id] = new RelationWindowState { ParentGuid = parentGuid };
        window = new DeferredReplayWindowToken(id, parentGuid, DeferredReplayBucketKind.AcceptedRelations);
        return [.. queue];
    }

    internal void RestoreDeferredAcceptedRelations(
        in DeferredReplayWindowToken window,
        ReadOnlySpan<DeferredAcceptedParentRelation> entries)
    {
        if (window.Kind != DeferredReplayBucketKind.AcceptedRelations
            || !_relationWindows.Remove(window.Id, out RelationWindowState? state))
        {
            return;
        }
        if (entries.Length == 0)
            return;
        IEnumerable<DeferredAcceptedParentRelation> filtered = entries.ToArray();
        foreach (Func<DeferredAcceptedParentRelation, bool> filter in state.Filters)
            filtered = filtered.Where(filter);
        DeferredAcceptedParentRelation[] survivors = filtered.ToArray();
        if (survivors.Length == 0)
            return;
        var restored = new Queue<DeferredAcceptedParentRelation>(survivors.Length);
        foreach (DeferredAcceptedParentRelation entry in survivors)
            restored.Enqueue(entry);
        if (_deferredAcceptedRelationsByParent.TryGetValue(
                window.ParentGuid,
                out Queue<DeferredAcceptedParentRelation>? existing))
        {
            foreach (DeferredAcceptedParentRelation entry in existing)
                restored.Enqueue(entry);
        }
        _deferredAcceptedRelationsByParent[window.ParentGuid] = restored;
    }

    internal bool ContainsDeferredAcceptedRelation(
        uint childGuid,
        RuntimeEntityKey childKey)
    {
        foreach (Queue<DeferredAcceptedParentRelation> queue
            in _deferredAcceptedRelationsByParent.Values)
        {
            if (queue.Any(candidate =>
                    candidate.ChildGuid == childGuid
                    && candidate.ChildKey == childKey))
            {
                return true;
            }
        }
        return false;
    }

    internal void CancelDeferredChildGeneration(
        uint childGuid,
        ushort terminalInstanceSequence)
    {
        FilterDeferredCreates(
            candidate => candidate.Spawn.Guid != childGuid
                || PhysicsTimestampGate.IsNewer(
                    terminalInstanceSequence,
                    candidate.Spawn.InstanceSequence));
        FilterDeferredAcceptedRelations(
            candidate => candidate.ChildGuid != childGuid
                || PhysicsTimestampGate.IsNewer(
                    terminalInstanceSequence,
                    candidate.ChildKey.Incarnation));
    }

    public void AcceptCreateObjectRelation(ParentAttachmentRelation relation)
    {
        _stagedByChild[relation.ChildGuid] = relation;
    }

    public void Enqueue(ParentEvent.Parsed update)
    {
        if (!_unresolvedByChild.TryGetValue(update.ChildGuid, out Queue<ParentAttachmentRelation>? queue))
        {
            queue = new Queue<ParentAttachmentRelation>();
            _unresolvedByChild.Add(update.ChildGuid, queue);
        }

        queue.Enqueue(new ParentAttachmentRelation(
            update.ParentGuid,
            update.ChildGuid,
            update.ParentLocation,
            update.PlacementId,
            update.ParentInstanceSequence,
            update.ChildPositionSequence));
    }

    public void Resolve(
        uint childGuid,
        Func<uint, bool> isObjectKnown,
        Func<uint, ushort?> resolveInstance,
        Func<ParentEvent.Parsed, bool> accept)
    {
        if (_stagedByChild.ContainsKey(childGuid))
            return;
        if (!_unresolvedByChild.TryGetValue(childGuid, out Queue<ParentAttachmentRelation>? queue))
            return;
        int candidateCount = queue.Count;
        for (int i = 0; i < candidateCount; i++)
        {
            ParentAttachmentRelation relation = queue.Dequeue();
            ushort? parentInstance = resolveInstance(relation.ParentGuid);
            if (parentInstance is null)
            {
                queue.Enqueue(relation with { WaitOwner = ParentAttachmentWaitOwner.Parent });
                continue;
            }

            if (parentInstance.Value != relation.ParentInstanceSequence)
            {
                if (PhysicsTimestampGate.IsNewer(
                        relation.ParentInstanceSequence,
                        parentInstance.Value))
                {
                    continue;
                }

                queue.Enqueue(relation with { WaitOwner = ParentAttachmentWaitOwner.Parent });
                continue;
            }

            if (!isObjectKnown(childGuid))
            {
                queue.Enqueue(relation with { WaitOwner = ParentAttachmentWaitOwner.Child });
                continue;
            }

            var update = new ParentEvent.Parsed(
                relation.ParentGuid,
                relation.ChildGuid,
                relation.ParentLocation,
                relation.PlacementId,
                relation.ParentInstanceSequence,
                relation.ChildPositionSequence);
            if (!accept(update))
                continue;

            _stagedByChild[childGuid] = relation with
            {
                WaitOwner = ParentAttachmentWaitOwner.Unknown,
            };
            break;
        }

        if (queue.Count == 0)
            _unresolvedByChild.Remove(childGuid);
    }

    public bool TryGetProjection(uint childGuid, out ParentAttachmentRelation relation) =>
        _stagedByChild.TryGetValue(childGuid, out relation)
        || _recoveryByChild.TryGetValue(childGuid, out relation);

    public bool TryGetStagedProjection(
        uint childGuid,
        out ParentAttachmentRelation relation) =>
        _stagedByChild.TryGetValue(childGuid, out relation);

    public bool TryGetRecoveryProjection(
        uint childGuid,
        out ParentAttachmentRelation relation) =>
        _recoveryByChild.TryGetValue(childGuid, out relation);

    public void CopyPendingProjectionChildrenTo(List<uint> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (uint childGuid in _stagedByChild.Keys)
            destination.Add(childGuid);
        foreach (uint childGuid in _recoveryByChild.Keys)
        {
            if (!_stagedByChild.ContainsKey(childGuid))
                destination.Add(childGuid);
        }
    }

    public bool IsCommitted(ParentAttachmentRelation relation) =>
        _lastAcceptedByChild.TryGetValue(
            relation.ChildGuid,
            out ParentAttachmentRelation committed)
        && committed == relation;

    public bool HasCommittedParent(uint childGuid) =>
        _lastAcceptedByChild.ContainsKey(childGuid);

    public bool TryGetCommittedParent(
        uint childGuid,
        out uint parentGuid,
        out ushort parentInstanceSequence)
    {
        if (_lastAcceptedByChild.TryGetValue(
                childGuid,
                out ParentAttachmentRelation relation))
        {
            parentGuid = relation.ParentGuid;
            parentInstanceSequence = relation.ParentInstanceSequence;
            return true;
        }
        parentGuid = 0u;
        parentInstanceSequence = 0;
        return false;
    }

    public bool IsPending(
        ParentAttachmentRelation relation,
        ParentProjectionCandidateKind kind) =>
        (kind is ParentProjectionCandidateKind.Staged
            ? _stagedByChild
            : _recoveryByChild).TryGetValue(
                relation.ChildGuid,
                out ParentAttachmentRelation pending)
        && pending == relation;

    public void MarkProjected(
        ParentAttachmentRelation relation,
        ParentProjectionCandidateKind kind)
    {
        Dictionary<uint, ParentAttachmentRelation> source =
            kind is ParentProjectionCandidateKind.Staged
                ? _stagedByChild
                : _recoveryByChild;
        if (source.TryGetValue(
                relation.ChildGuid,
                out ParentAttachmentRelation pending)
            && pending == relation)
        {
            source.Remove(relation.ChildGuid);
        }
    }

    public bool CanCommitIncarnation(
        ParentAttachmentRelation relation,
        Func<uint, ushort?>? resolveParentInstance)
    {
        if (resolveParentInstance is null
            || resolveParentInstance(relation.ParentGuid) is not { } liveParentInstance
            || liveParentInstance == relation.ParentInstanceSequence)
        {
            return true;
        }

        if (_loggedIncarnationRefusals.Add(relation.ChildGuid))
        {
            Console.Error.WriteLine(
                $"[parent-attach] refused: child=0x{relation.ChildGuid:X8} names " +
                $"parent 0x{relation.ParentGuid:X8} incarnation " +
                $"{relation.ParentInstanceSequence}, but the parent's live " +
                $"incarnation is {liveParentInstance}. " +
                "Logged once for this child; further refusals for the same " +
                "child are suppressed.");
        }
        return false;
    }

    public bool CommitProjection(
        ParentAttachmentRelation relation,
        Func<uint, ushort?>? resolveParentInstance = null)
    {
        if (!_stagedByChild.TryGetValue(
                relation.ChildGuid,
                out ParentAttachmentRelation staged)
            || staged != relation)
        {
            return false;
        }

        if (!CanCommitIncarnation(relation, resolveParentInstance))
            return false;

        RemoveCommittedChild(relation.ChildGuid);
        _lastAcceptedByChild[relation.ChildGuid] = relation;
        var parent = new ParentIncarnation(
            relation.ParentGuid,
            relation.ParentInstanceSequence);
        if (!_committedChildrenByParent.TryGetValue(
                parent,
                out List<uint>? children))
        {
            children = [];
            _committedChildrenByParent.Add(parent, children);
        }
        children.Add(relation.ChildGuid);
        _stagedByChild.Remove(relation.ChildGuid);
        _recoveryByChild[relation.ChildGuid] = relation;
        return true;
    }

    public void RejectProjection(ParentAttachmentRelation relation)
    {
        if (_stagedByChild.TryGetValue(
                relation.ChildGuid,
                out ParentAttachmentRelation staged)
            && staged == relation)
        {
            _stagedByChild.Remove(relation.ChildGuid);
        }
    }

    public bool RestoreLastAccepted(uint childGuid)
    {
        if (!_lastAcceptedByChild.TryGetValue(childGuid, out ParentAttachmentRelation relation))
            return false;
        _recoveryByChild[childGuid] = relation;
        return true;
    }

    public IReadOnlyList<uint> ChildrenWaitingForParent(uint parentGuid)
    {
        var result = new HashSet<uint>();
        foreach ((uint childGuid, ParentAttachmentRelation relation) in _stagedByChild)
        {
            if (relation.ParentGuid == parentGuid)
                result.Add(childGuid);
        }

        foreach ((uint childGuid, ParentAttachmentRelation relation) in _recoveryByChild)
        {
            if (relation.ParentGuid == parentGuid)
                result.Add(childGuid);
        }

        foreach ((uint childGuid, Queue<ParentAttachmentRelation> queue) in _unresolvedByChild)
        {
            if (queue.Any(relation => relation.ParentGuid == parentGuid))
                result.Add(childGuid);
        }

        return result.ToArray();
    }

    public IReadOnlyList<uint> ChildrenUnresolvedForParent(uint parentGuid)
    {
        List<uint>? result = null;
        foreach ((uint childGuid, Queue<ParentAttachmentRelation> queue) in _unresolvedByChild)
        {
            if (queue.Any(relation => relation.ParentGuid == parentGuid))
            {
                result ??= new List<uint>();
                result.Add(childGuid);
            }
        }
        return (IReadOnlyList<uint>?)result ?? Array.Empty<uint>();
    }

    public IReadOnlyList<uint> ChildrenAttachedToParent(
        uint parentGuid,
        ushort parentInstanceSequence)
    {
        var parent = new ParentIncarnation(parentGuid, parentInstanceSequence);
        return _committedChildrenByParent.TryGetValue(
            parent,
            out List<uint>? children)
                ? children
                : Array.Empty<uint>();
    }

    public void RemoveObject(uint guid)
    {
        RemoveDeferredChildCreates(guid);
        RemoveDeferredAcceptedRelationsForChild(guid);
        _stagedByChild.Remove(guid);
        _recoveryByChild.Remove(guid);
        RemoveCommittedChild(guid);
        _unresolvedByChild.Remove(guid);

        RemoveParentReferences(_stagedByChild, guid);
        RemoveParentReferences(_recoveryByChild, guid);
        RemoveCommittedParentReferences(guid);

        uint[] children = _unresolvedByChild.Keys.ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            uint childGuid = children[i];
            Queue<ParentAttachmentRelation> retained = new(
                _unresolvedByChild[childGuid].Where(relation => relation.ParentGuid != guid));
            if (retained.Count == 0)
                _unresolvedByChild.Remove(childGuid);
            else
                _unresolvedByChild[childGuid] = retained;
        }
    }

    /// <summary>
    /// Ends one logical incarnation without discarding unresolved wire events
    /// that may explicitly address the replacement/future parent generation.
    /// </summary>
    public void EndGeneration(uint guid, ushort replacementGeneration)
    {
        FilterDeferredCreates(candidate =>
            candidate.Spawn.Guid != guid
            || candidate.Spawn.InstanceSequence == replacementGeneration
            || PhysicsTimestampGate.IsNewer(
                replacementGeneration,
                candidate.Spawn.InstanceSequence));
        FilterDeferredAcceptedRelations(candidate =>
            candidate.ChildGuid != guid
            || candidate.ChildKey.Incarnation == replacementGeneration
            || PhysicsTimestampGate.IsNewer(
                replacementGeneration,
                candidate.ChildKey.Incarnation));
        FilterChildCandidates(
            guid,
            relation => relation.WaitOwner is ParentAttachmentWaitOwner.Parent);
        _stagedByChild.Remove(guid);
        _recoveryByChild.Remove(guid);
        RemoveCommittedChild(guid);
        RemoveParentReferences(_stagedByChild, guid);
        RemoveParentReferences(_recoveryByChild, guid);
        RemoveCommittedParentReferences(guid);
        FilterParentCandidates(
            guid,
            relation => relation.ParentInstanceSequence == replacementGeneration
                || PhysicsTimestampGate.IsNewer(
                    replacementGeneration,
                    relation.ParentInstanceSequence));
    }

    public void DeleteGeneration(uint guid, ushort deletedGeneration)
    {
        CancelDeferredChildGeneration(guid, deletedGeneration);
        FilterChildCandidates(
            guid,
            relation => relation.WaitOwner is ParentAttachmentWaitOwner.Parent);
        _stagedByChild.Remove(guid);
        _recoveryByChild.Remove(guid);
        RemoveCommittedChild(guid);
        RemoveParentReferences(_stagedByChild, guid);
        RemoveParentReferences(_recoveryByChild, guid);
        RemoveCommittedParentReferences(guid);
        FilterParentCandidates(
            guid,
            relation => PhysicsTimestampGate.IsNewer(
                deletedGeneration,
                relation.ParentInstanceSequence));
    }

    public void EndChildProjection(uint childGuid)
    {
        _stagedByChild.Remove(childGuid);
        _recoveryByChild.Remove(childGuid);
        RemoveCommittedChild(childGuid);
    }

    public void RemoveChild(uint childGuid)
    {
        RemoveDeferredChildCreates(childGuid);
        RemoveDeferredAcceptedRelationsForChild(childGuid);
        _stagedByChild.Remove(childGuid);
        _recoveryByChild.Remove(childGuid);
        RemoveCommittedChild(childGuid);
        _unresolvedByChild.Remove(childGuid);
        _loggedIncarnationRefusals.Remove(childGuid);
    }

    public void Clear()
    {
        _deferredCreatesByParent.Clear();
        _deferredAcceptedRelationsByParent.Clear();
        _createWindows.Clear();
        _relationWindows.Clear();
        _unresolvedByChild.Clear();
        _stagedByChild.Clear();
        _recoveryByChild.Clear();
        _lastAcceptedByChild.Clear();
        foreach (List<uint> children in _committedChildrenByParent.Values)
            children.Clear();
        _committedChildrenByParent.Clear();
        _loggedIncarnationRefusals.Clear();
    }

    private void RemoveDeferredChildCreates(uint childGuid)
        => FilterDeferredCreates(
            candidate => candidate.Spawn.Guid != childGuid);

    private void RemoveDeferredAcceptedRelationsForChild(uint childGuid)
        => FilterDeferredAcceptedRelations(
            candidate => candidate.ChildGuid != childGuid);

    private void FilterDeferredCreates(
        Func<DeferredParentCreate, bool> retain)
    {
        uint[] parents = _deferredCreatesByParent.Keys.ToArray();
        for (int index = 0; index < parents.Length; index++)
        {
            uint parentGuid = parents[index];
            Queue<DeferredParentCreate> retained = new(
                _deferredCreatesByParent[parentGuid].Where(retain));
            if (retained.Count == 0)
                _deferredCreatesByParent.Remove(parentGuid);
            else
                _deferredCreatesByParent[parentGuid] = retained;
        }
        foreach (CreateWindowState state in _createWindows.Values)
            state.Filters.Add(retain);
    }

    private void FilterDeferredAcceptedRelations(
        Func<DeferredAcceptedParentRelation, bool> retain)
    {
        uint[] parents = _deferredAcceptedRelationsByParent.Keys.ToArray();
        for (int index = 0; index < parents.Length; index++)
        {
            uint parentGuid = parents[index];
            Queue<DeferredAcceptedParentRelation> retained = new(
                _deferredAcceptedRelationsByParent[parentGuid].Where(retain));
            if (retained.Count == 0)
                _deferredAcceptedRelationsByParent.Remove(parentGuid);
            else
                _deferredAcceptedRelationsByParent[parentGuid] = retained;
        }
        foreach (RelationWindowState state in _relationWindows.Values)
            state.Filters.Add(retain);
    }

    private void RemoveCommittedChild(uint childGuid)
    {
        if (!_lastAcceptedByChild.Remove(
                childGuid,
                out ParentAttachmentRelation relation))
        {
            return;
        }

        var parent = new ParentIncarnation(
            relation.ParentGuid,
            relation.ParentInstanceSequence);
        if (!_committedChildrenByParent.TryGetValue(
                parent,
                out List<uint>? children))
        {
            return;
        }

        int childIndex = children.IndexOf(childGuid);
        if (childIndex >= 0)
        {
            int lastIndex = children.Count - 1;
            children[childIndex] = children[lastIndex];
            children.RemoveAt(lastIndex);
        }

        if (children.Count == 0)
            _committedChildrenByParent.Remove(parent);
    }

    private void RemoveCommittedParentReferences(uint parentGuid)
    {
        uint[] children = _lastAcceptedByChild
            .Where(pair => pair.Value.ParentGuid == parentGuid)
            .Select(pair => pair.Key)
            .ToArray();
        for (int i = 0; i < children.Length; i++)
            RemoveCommittedChild(children[i]);
    }

    private static void RemoveParentReferences(
        Dictionary<uint, ParentAttachmentRelation> relations,
        uint parentGuid)
    {
        uint[] children = relations
            .Where(pair => pair.Value.ParentGuid == parentGuid)
            .Select(pair => pair.Key)
            .ToArray();
        for (int i = 0; i < children.Length; i++)
            relations.Remove(children[i]);
    }

    private void FilterParentCandidates(
        uint parentGuid,
        Func<ParentAttachmentRelation, bool> retain)
    {
        uint[] children = _unresolvedByChild.Keys.ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            uint childGuid = children[i];
            Queue<ParentAttachmentRelation> queue = _unresolvedByChild[childGuid];
            var retained = new Queue<ParentAttachmentRelation>(
                queue.Where(relation => relation.ParentGuid != parentGuid || retain(relation)));
            if (retained.Count == 0)
                _unresolvedByChild.Remove(childGuid);
            else
                _unresolvedByChild[childGuid] = retained;
        }
    }

    private void FilterChildCandidates(
        uint childGuid,
        Func<ParentAttachmentRelation, bool> retain)
    {
        if (!_unresolvedByChild.TryGetValue(
                childGuid,
                out Queue<ParentAttachmentRelation>? queue))
        {
            return;
        }

        var retained = new Queue<ParentAttachmentRelation>(queue.Where(retain));
        if (retained.Count == 0)
            _unresolvedByChild.Remove(childGuid);
        else
            _unresolvedByChild[childGuid] = retained;
    }

    private readonly record struct ParentIncarnation(
        uint ServerGuid,
        ushort InstanceSequence);
}

internal readonly record struct DeferredParentCreate(
    ulong AdmissionId,
    WorldSession.EntitySpawn Spawn,
    bool IsLocalPlayer)
{
    internal bool IsValid => AdmissionId != 0UL
        && Spawn.Guid != 0u;
}

internal readonly record struct DeferredAcceptedParentRelation(
    ulong AdmissionId,
    uint ChildGuid,
    RuntimeEntityKey ChildKey,
    ParentEvent.Parsed? Standalone,
    CreateParentUpdate? Envelope,
    AcceptedPhysicsTimestamps AcceptedTimestamps)
{
    internal bool IsValid => AdmissionId != 0UL
        && ChildGuid != 0u
        && ChildKey.LocalEntityId != 0u
        && (Standalone.HasValue ^ Envelope.HasValue);

    internal ushort? ParentInstanceSequence => Standalone?.ParentInstanceSequence;
}

internal enum DeferredReplayBucketKind : byte
{
    Creates,
    AcceptedRelations,
}

internal readonly record struct DeferredReplayWindowToken(
    ulong Id,
    uint ParentGuid,
    DeferredReplayBucketKind Kind)
{
    internal bool IsValid => Id != 0UL;
}

public readonly record struct ParentAttachmentRelation(
    uint ParentGuid,
    uint ChildGuid,
    uint ParentLocation,
    uint PlacementId,
    ushort ParentInstanceSequence,
    ushort ChildPositionSequence,
    ParentAttachmentWaitOwner WaitOwner = ParentAttachmentWaitOwner.Unknown);

public enum ParentAttachmentWaitOwner
{
    Unknown,
    Parent,
    Child,
}

public enum ParentProjectionCandidateKind
{
    Staged,
    Recovery,
}
