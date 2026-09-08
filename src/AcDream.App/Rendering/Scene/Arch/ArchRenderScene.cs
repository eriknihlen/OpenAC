using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AcDream.App.Rendering;
using Arch.Core;
using ArchWorld = Arch.Core.World;

namespace AcDream.App.Rendering.Scene.Arch;

internal sealed class ArchRenderScene : IRenderScene, IRenderSceneQuerySource
{
    private const int InitialEntityCapacity = 256;
    private const int InitialArchetypeCapacity = 8;

    private readonly int _ownerThreadId;
    private readonly Dictionary<RenderProjectionId, SceneEntry> _entries = [];
    private readonly HashSet<RenderProjectionId> _outdoorStatics = [];
    private readonly HashSet<RenderProjectionId> _indoorCellStatics = [];
    private readonly HashSet<RenderProjectionId> _dynamics = [];
    private readonly HashSet<RenderProjectionId> _outdoorDynamics = [];
    private readonly HashSet<RenderProjectionId> _portalStraddlingDynamics = [];
    private readonly HashSet<RenderProjectionId> _translucent = [];
    private readonly HashSet<RenderProjectionId> _selectable = [];
    private readonly HashSet<RenderProjectionId> _lightCandidates = [];
    private readonly HashSet<RenderProjectionId> _dirty = [];
    private readonly Dictionary<uint, RenderProjectionId> _byLocalEntityId = [];
    private ArchWorld _world;
    private RenderProjectionCounts _counts;
    private ulong _lastAppliedJournalSequence;
    private ulong _indexRevision = 1;
    private ulong _directionalShadowTopologyRevision = 1;
    private DirectionalShadowTransformChange[]? _directionalShadowTransformChanges;
    private Dictionary<RenderProjectionId, DirectionalShadowPartPoseSnapshot>?
        _directionalShadowPartPoses;
    private ulong _directionalShadowTransformRevision;
    private int _directionalShadowTransformChangeCount;
    private bool _disposed;

    public ArchRenderScene(RenderSceneGeneration initialGeneration)
    {
        Generation = initialGeneration;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _world = CreateWorld();
    }

    public RenderSceneGeneration Generation { get; private set; }

    public RenderProjectionCounts Counts
    {
        get
        {
            EnsureAvailable();
            return _counts;
        }
    }

    public RenderSceneMemoryAccounting Memory
    {
        get
        {
            EnsureAvailable();

            int allocatedChunkCount = 0;
            long estimatedChunkPayloadBytes = 0;
            foreach (Archetype archetype in _world.Archetypes.AsSpan())
            {
                allocatedChunkCount += archetype.ChunkCount;
                estimatedChunkPayloadBytes +=
                    (long)archetype.ChunkSize * archetype.ChunkCount;
            }

            int lookupCapacity = _entries.EnsureCapacity(0);
            long lookupBytes =
                (long)lookupCapacity * Unsafe.SizeOf<ProjectionLookupSlotEstimate>();
            long indexBytes = EstimateIndexBytes();
            long directionalShadowJournalBytes =
                _directionalShadowTransformChanges is null
                    ? 0
                    : checked((long)_directionalShadowTransformChanges.Length
                        * Unsafe.SizeOf<DirectionalShadowTransformChange>());
            if (_directionalShadowPartPoses is not null)
            {
                directionalShadowJournalBytes = checked(
                    directionalShadowJournalBytes
                    + (long)_directionalShadowPartPoses.EnsureCapacity(0)
                        * (sizeof(int)
                            + Unsafe.SizeOf<KeyValuePair<
                                RenderProjectionId,
                                DirectionalShadowPartPoseSnapshot>>())
                    + _directionalShadowPartPoses.Values.Sum(static pose =>
                        (long)pose.Count * Unsafe.SizeOf<Matrix4x4>()));
            }

            return new RenderSceneMemoryAccounting(
                EntityCount: _world.Size,
                ArchEntityCapacity: _world.Capacity,
                ArchetypeCount: _world.Archetypes.Count,
                AllocatedChunkCount: allocatedChunkCount,
                EstimatedChunkPayloadBytes: estimatedChunkPayloadBytes,
                ProjectionLookupCapacity: lookupCapacity,
                EstimatedProjectionLookupBytes: lookupBytes,
                EstimatedIndexBytes: indexBytes,
                EstimatedJournalBufferBytes: directionalShadowJournalBytes,
                EstimatedSynchronizationSourceBytes: 0);
        }
    }

    public RenderDeltaApplyResult Apply(
        ReadOnlySpan<RenderProjectionDelta> deltas)
    {
        EnsureMutationThread();
        var result = new ApplyResultBuilder();

        foreach (ref readonly RenderProjectionDelta delta in deltas)
        {
            if (delta.Generation != Generation)
            {
                result.RejectedGeneration++;
                continue;
            }

            if (delta.JournalSequence == 0
                || delta.JournalSequence <= _lastAppliedJournalSequence)
            {
                result.RejectedOutOfOrderSequence++;
                continue;
            }

            _lastAppliedJournalSequence = delta.JournalSequence;
            RenderProjectionRecord record = delta.Record;

            switch (delta.Kind)
            {
                case RenderProjectionDeltaKind.Register:
                    ApplyRegister(in record, ref result);
                    break;
                case RenderProjectionDeltaKind.UpdateTransform:
                case RenderProjectionDeltaKind.UpdateAppearance:
                case RenderProjectionDeltaKind.UpdateFlags:
                case RenderProjectionDeltaKind.Rebucket:
                    ApplyUpdate(delta.Kind, in record, ref result);
                    break;
                case RenderProjectionDeltaKind.Unregister:
                    ApplyUnregister(in record, ref result);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(delta.Kind),
                        delta.Kind,
                        null);
            }
        }

        return result.Build();
    }

    public void SynchronizeDynamicSources(
        in DynamicProjectionSyncInput input)
    {
        EnsureMutationThread();
        if (input.Generation != Generation)
        {
            throw new InvalidOperationException(
                $"Dynamic synchronization belongs to {input.Generation}, "
                + $"but the scene is {Generation}.");
        }

        foreach (ref readonly DynamicProjectionUpdate update in input.Updates)
        {
            if (!_entries.TryGetValue(update.Id, out SceneEntry entry)
                || entry.OwnerIncarnation != update.OwnerIncarnation)
            {
                continue;
            }

            ref RenderTransform current =
                ref _world.Get<RenderTransform>(entry.Entity);
            ref RenderWorldBounds bounds =
                ref _world.Get<RenderWorldBounds>(entry.Entity);
            bool transformChanged = !TransformBitsEqual(current, update.Transform);
            if (!transformChanged && bounds == update.Bounds)
                continue;

            _world.Set(
                entry.Entity,
                new PreviousRenderTransform(current.LocalToWorld));
            _world.Set(entry.Entity, update.Transform);
            _world.Set(entry.Entity, update.Bounds);
            if (transformChanged
                && HasRefreshableDirectionalShadowTransforms(entry.ProjectionClass)
                && _directionalShadowTransformChanges is not null)
            {
                RenderProjectionRecord currentRecord = ReadRecord(in entry);
                PublishDirectionalShadowTransformChange(
                    in currentRecord,
                    DirectionalShadowTransformChangeKind.DynamicSynchronization);
            }

            ref RenderDirtyMask dirty =
                ref _world.Get<RenderDirtyMask>(entry.Entity);
            dirty |= RenderDirtyMask.Transform | RenderDirtyMask.WorldBounds;
            _dirty.Add(update.Id);
        }
    }

    public RenderSceneDigest BuildDigest(RenderSceneDigestBuffer reuse)
    {
        ArgumentNullException.ThrowIfNull(reuse);
        EnsureAvailable();

        List<RenderProjectionRecord> records = reuse.Records;
        records.Clear();
        if (records.Capacity < _entries.Count)
            records.Capacity = _entries.Count;

        foreach (SceneEntry entry in _entries.Values)
            records.Add(ReadRecord(in entry));

        records.Sort(RenderProjectionRecordComparer.Instance);

        StableRenderHash128 hash = StableRenderHash128.Create();
        hash.Add(Generation.RawValue);
        hash.Add(records.Count);
        foreach (RenderProjectionRecord record in records)
            AddRecord(ref hash, in record);

        return new RenderSceneDigest(Generation, _counts, hash.Finish());
    }

    public RenderSceneQuery OpenQuery()
    {
        EnsureAvailable();
        return new RenderSceneQuery(this, Generation);
    }

    public void Clear(RenderSceneGeneration replacementGeneration)
    {
        EnsureMutationThread();
        if (replacementGeneration.CompareTo(Generation) <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacementGeneration),
                replacementGeneration,
                "A replacement render-scene generation must advance.");
        }

        ArchWorld.Destroy(_world);
        _world = CreateWorld();
        _entries.Clear();
        ClearIndices();
        _counts = default;
        _lastAppliedJournalSequence = 0;
        ResetDirectionalShadowTransformChanges();
        Generation = replacementGeneration;
        AdvanceIndexRevision();
        AdvanceDirectionalShadowTopologyRevision();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        EnsureMutationThread();
        ArchWorld.Destroy(_world);
        _entries.Clear();
        ClearIndices();
        _directionalShadowTransformChanges = null;
        _directionalShadowPartPoses = null;
        _directionalShadowTransformRevision = 0;
        _directionalShadowTransformChangeCount = 0;
        _counts = default;
        _disposed = true;
    }

    public void ClearDirty()
    {
        EnsureMutationThread();
        foreach (RenderProjectionId id in _dirty)
        {
            if (_entries.TryGetValue(id, out SceneEntry entry))
                _world.Set(entry.Entity, RenderDirtyMask.None);
        }
        _dirty.Clear();
    }

    RenderProjectionCounts IRenderSceneQuerySource.GetCounts(
        RenderSceneGeneration generation)
    {
        EnsureQueryGeneration(generation);
        return _counts;
    }

    RenderSceneIndexCounts IRenderSceneQuerySource.GetIndexCounts(
        RenderSceneGeneration generation)
    {
        EnsureQueryGeneration(generation);
        return new RenderSceneIndexCounts(
            _outdoorStatics.Count,
            _indoorCellStatics.Count,
            _dynamics.Count,
            _outdoorDynamics.Count,
            _portalStraddlingDynamics.Count,
            _translucent.Count,
            _selectable.Count,
            _lightCandidates.Count,
            _dirty.Count);
    }

    ulong IRenderSceneQuerySource.GetIndexRevision(
        RenderSceneGeneration generation)
    {
        EnsureQueryGeneration(generation);
        return _indexRevision;
    }

    ulong IRenderSceneQuerySource.GetDirectionalShadowTopologyRevision(
        RenderSceneGeneration generation)
    {
        EnsureQueryGeneration(generation);
        return _directionalShadowTopologyRevision;
    }

    ulong IRenderSceneQuerySource.GetDirectionalShadowTransformRevision(
        RenderSceneGeneration generation)
    {
        EnsureQueryGeneration(generation);
        EnsureDirectionalShadowTransformJournal();
        return _directionalShadowTransformRevision;
    }

    DirectionalShadowTransformChanges
        IRenderSceneQuerySource.CopyDirectionalShadowTransformChanges(
            RenderSceneGeneration generation,
            ulong afterRevision,
            Span<DirectionalShadowTransformSnapshot> destination)
    {
        EnsureQueryGeneration(generation);
        EnsureDirectionalShadowTransformJournal();
        ulong latest = _directionalShadowTransformRevision;
        if (afterRevision == latest)
            return new DirectionalShadowTransformChanges(latest, 0, false);
        if (afterRevision == 0
            || afterRevision > latest
            || latest - afterRevision
                > checked((ulong)_directionalShadowTransformChangeCount))
        {
            return new DirectionalShadowTransformChanges(latest, 0, true);
        }

        int count = checked((int)(latest - afterRevision));
        if (destination.Length < count)
            return new DirectionalShadowTransformChanges(latest, 0, true);
        DirectionalShadowTransformChange[] journal =
            _directionalShadowTransformChanges!;
        int updateTransformCount = 0;
        int updateAppearanceCount = 0;
        int dynamicSynchronizationCount = 0;
        int activeAnimatedStaticCount = 0;
        int liveDynamicRootCount = 0;
        int equippedChildCount = 0;
        for (int index = 0; index < count; index++)
        {
            ulong revision = checked(afterRevision + (ulong)index + 1UL);
            DirectionalShadowTransformChange change =
                journal[(int)(revision % (ulong)journal.Length)];
            if (change.Revision != revision)
                return new DirectionalShadowTransformChanges(latest, 0, true);
            destination[index] = change.Projection;
            switch (change.Kind)
            {
                case DirectionalShadowTransformChangeKind.UpdateTransform:
                    updateTransformCount++;
                    break;
                case DirectionalShadowTransformChangeKind.UpdateAppearance:
                    updateAppearanceCount++;
                    break;
                case DirectionalShadowTransformChangeKind.DynamicSynchronization:
                    dynamicSynchronizationCount++;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown directional-shadow change kind {change.Kind}.");
            }
            switch (change.Projection.ProjectionClass)
            {
                case RenderProjectionClass.ActiveAnimatedStatic:
                    activeAnimatedStaticCount++;
                    break;
                case RenderProjectionClass.LiveDynamicRoot:
                    liveDynamicRootCount++;
                    break;
                case RenderProjectionClass.EquippedChild:
                    equippedChildCount++;
                    break;
            }
        }
        return new DirectionalShadowTransformChanges(
            latest,
            count,
            false,
            updateTransformCount,
            updateAppearanceCount,
            dynamicSynchronizationCount,
            activeAnimatedStaticCount,
            liveDynamicRootCount,
            equippedChildCount);
    }

    bool IRenderSceneQuerySource.TryGet(
        RenderSceneGeneration generation,
        RenderProjectionId id,
        out RenderProjectionRecord record)
    {
        EnsureQueryGeneration(generation);
        if (_entries.TryGetValue(id, out SceneEntry entry))
        {
            record = ReadRecord(in entry);
            return true;
        }

        record = default;
        return false;
    }

    bool IRenderSceneQuerySource.TryGetByLocalEntityId(
        RenderSceneGeneration generation,
        uint localEntityId,
        out RenderProjectionRecord record)
    {
        EnsureQueryGeneration(generation);
        if (_byLocalEntityId.TryGetValue(localEntityId, out RenderProjectionId id)
            && _entries.TryGetValue(id, out SceneEntry entry))
        {
            record = ReadRecord(in entry);
            return true;
        }

        record = default;
        return false;
    }

    int IRenderSceneQuerySource.CopyById(
        RenderSceneGeneration generation,
        ReadOnlySpan<RenderProjectionId> ids,
        Span<RenderProjectionRecord> destination)
    {
        EnsureQueryGeneration(generation);
        if (destination.Length < ids.Length)
        {
            throw new ArgumentException(
                "The render-scene ID-copy destination is too small.",
                nameof(destination));
        }
        for (int index = 0; index < ids.Length; index++)
        {
            if (!_entries.TryGetValue(ids[index], out SceneEntry entry))
            {
                throw new InvalidOperationException(
                    $"Render-scene projection {ids[index]} disappeared during a batched copy.");
            }
            destination[index] = ReadRecord(in entry);
        }
        return ids.Length;
    }

    int IRenderSceneQuerySource.CopyTo(
        RenderSceneGeneration generation,
        RenderProjectionClass? projectionClass,
        Span<RenderProjectionRecord> destination)
    {
        EnsureQueryGeneration(generation);
        int required = projectionClass.HasValue
            ? _counts.For(projectionClass.Value)
            : _counts.Total;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} records; {required} required.",
                nameof(destination));
        }

        int count = 0;
        foreach (SceneEntry entry in _entries.Values)
        {
            if (projectionClass.HasValue
                && entry.ProjectionClass != projectionClass.Value)
            {
                continue;
            }

            destination[count++] = ReadRecord(in entry);
        }

        return count;
    }

    int IRenderSceneQuerySource.CopyIndexTo(
        RenderSceneGeneration generation,
        RenderSceneIndex index,
        Span<RenderProjectionRecord> destination)
    {
        EnsureQueryGeneration(generation);
        HashSet<RenderProjectionId> source = Index(index);
        return CopyIdsTo(source, destination);
    }

    private static ArchWorld CreateWorld() =>
        ArchWorld.Create(
            archetypeCapacity: InitialArchetypeCapacity,
            entityCapacity: InitialEntityCapacity);

    private void ApplyRegister(
        in RenderProjectionRecord record,
        ref ApplyResultBuilder result)
    {
        if (_entries.TryGetValue(record.Id, out SceneEntry existing))
        {
            int incarnationOrder =
                record.OwnerIncarnation.CompareTo(existing.OwnerIncarnation);
            if (incarnationOrder < 0)
            {
                result.RejectedStaleIncarnation++;
                return;
            }

            if (incarnationOrder == 0
                && record.ProjectionClass == existing.ProjectionClass)
            {
                RenderProjectionRecord prior = ReadRecord(in existing);
                WriteRecord(existing.Entity, in record);
                UpdateIndices(in prior, in record);
                if (HasRefreshableDirectionalShadowTransforms(record.ProjectionClass))
                {
                    if (!TransformBitsEqual(prior.Transform, record.Transform))
                    {
                        PublishDirectionalShadowTransformChange(
                            in record,
                            DirectionalShadowTransformChangeKind.UpdateTransform);
                    }
                    PublishDirectionalShadowPartPoseChangeIfNeeded(in record);
                }
                result.Applied++;
                result.Updated++;
                return;
            }

            Destroy(in existing);
            result.Replaced++;
        }

        Entity entity = CreateEntity(in record);
        _entries[record.Id] = new SceneEntry(
            entity,
            record.OwnerIncarnation,
            record.ProjectionClass);
        IncrementCount(record.ProjectionClass);
        AddToIndices(in record);
        SynchronizeDirectionalShadowPartPose(in record);
        result.Applied++;
        result.Registered++;
    }

    private void ApplyUpdate(
        RenderProjectionDeltaKind kind,
        in RenderProjectionRecord record,
        ref ApplyResultBuilder result)
    {
        if (!TryGetCurrent(in record, ref result, out SceneEntry entry))
            return;

        RenderProjectionRecord prior = ReadRecord(in entry);
        switch (kind)
        {
            case RenderProjectionDeltaKind.UpdateTransform:
                _world.Set(entry.Entity, record.PreviousTransform);
                _world.Set(entry.Entity, record.Transform);
                _world.Set(entry.Entity, record.Bounds);
                _world.Set(entry.Entity, record.SortKey);
                _world.Set(entry.Entity, record.Source);
                OrDirty(
                    entry.Entity,
                    record.Id,
                    RenderDirtyMask.Transform
                    | RenderDirtyMask.WorldBounds
                    | RenderDirtyMask.SortKey);
                break;
            case RenderProjectionDeltaKind.UpdateAppearance:
                _world.Set(entry.Entity, record.MeshSet);
                _world.Set(entry.Entity, record.Material);
                _world.Set(entry.Entity, record.DegradeState);
                _world.Set(entry.Entity, record.Source);
                _world.Set(entry.Entity, record.EntityPayload);
                OrDirty(
                    entry.Entity,
                    record.Id,
                    RenderDirtyMask.Appearance);
                break;
            case RenderProjectionDeltaKind.UpdateFlags:
                _world.Set(entry.Entity, record.Flags);
                _world.Set(entry.Entity, record.Source);
                OrDirty(entry.Entity, record.Id, RenderDirtyMask.Flags);
                break;
            case RenderProjectionDeltaKind.Rebucket:
                _world.Set(entry.Entity, record.Residency);
                _world.Set(entry.Entity, record.Source);
                OrDirty(
                    entry.Entity,
                    record.Id,
                    RenderDirtyMask.SpatialResidency);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        RenderProjectionRecord current = ReadRecord(in entry);
        UpdateIndices(in prior, in current);
        if (HasRefreshableDirectionalShadowTransforms(current.ProjectionClass))
        {
            if (kind is RenderProjectionDeltaKind.UpdateTransform
                && !TransformBitsEqual(prior.Transform, current.Transform))
            {
                PublishDirectionalShadowTransformChange(
                    in current,
                    DirectionalShadowTransformChangeKind.UpdateTransform);
            }
            else if (kind is RenderProjectionDeltaKind.UpdateAppearance)
            {
                PublishDirectionalShadowPartPoseChangeIfNeeded(in current);
            }
        }
        result.Applied++;
        result.Updated++;
    }

    private void ApplyUnregister(
        in RenderProjectionRecord record,
        ref ApplyResultBuilder result)
    {
        if (!TryGetCurrent(in record, ref result, out SceneEntry entry))
            return;

        Destroy(in entry);
        _entries.Remove(record.Id);
        result.Applied++;
        result.Unregistered++;
    }

    private bool TryGetCurrent(
        in RenderProjectionRecord record,
        ref ApplyResultBuilder result,
        out SceneEntry entry)
    {
        if (!_entries.TryGetValue(record.Id, out entry))
        {
            result.RejectedMissing++;
            return false;
        }

        if (record.OwnerIncarnation != entry.OwnerIncarnation)
        {
            result.RejectedStaleIncarnation++;
            return false;
        }

        return true;
    }

    private Entity CreateEntity(in RenderProjectionRecord record) =>
        record.ProjectionClass switch
        {
            RenderProjectionClass.OutdoorStatic =>
                CreateEntity(in record, new OutdoorStaticTag()),
            RenderProjectionClass.IndoorCellStatic =>
                CreateEntity(in record, new IndoorCellStaticTag()),
            RenderProjectionClass.LiveDynamicRoot =>
                CreateEntity(in record, new LiveDynamicRootTag()),
            RenderProjectionClass.ActiveAnimatedStatic =>
                CreateEntity(in record, new ActiveAnimatedStaticTag()),
            RenderProjectionClass.EquippedChild =>
                CreateEntity(in record, new EquippedChildTag()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(record.ProjectionClass),
                record.ProjectionClass,
                null),
        };

    private Entity CreateEntity<TTag>(
        in RenderProjectionRecord record,
        TTag tag)
        where TTag : struct =>
        _world.Create(
            new ProjectionIdentity(record.Id),
            record.Transform,
            record.PreviousTransform,
            record.MeshSet,
            record.Material,
            record.Residency,
            record.Bounds,
            record.Flags,
            record.DegradeState,
            record.SortKey,
            record.OwnerIncarnation,
            record.DirtyMask,
            record.Source,
            record.EntityPayload,
            tag);

    private void WriteRecord(
        Entity entity,
        in RenderProjectionRecord record)
    {
        _world.Set(entity, record.Transform);
        _world.Set(entity, record.PreviousTransform);
        _world.Set(entity, record.MeshSet);
        _world.Set(entity, record.Material);
        _world.Set(entity, record.Residency);
        _world.Set(entity, record.Bounds);
        _world.Set(entity, record.Flags);
        _world.Set(entity, record.DegradeState);
        _world.Set(entity, record.SortKey);
        _world.Set(entity, record.OwnerIncarnation);
        _world.Set(entity, record.DirtyMask);
        _world.Set(entity, record.Source);
        _world.Set(entity, record.EntityPayload);
    }

    private RenderProjectionRecord ReadRecord(in SceneEntry entry) =>
        new(
            _world.Get<ProjectionIdentity>(entry.Entity).Id,
            entry.ProjectionClass,
            _world.Get<RenderOwnerIncarnation>(entry.Entity),
            _world.Get<RenderTransform>(entry.Entity),
            _world.Get<PreviousRenderTransform>(entry.Entity),
            _world.Get<RenderMeshSet>(entry.Entity),
            _world.Get<RenderMaterialVariant>(entry.Entity),
            _world.Get<RenderSpatialResidency>(entry.Entity),
            _world.Get<RenderWorldBounds>(entry.Entity),
            _world.Get<RenderProjectionFlags>(entry.Entity),
            _world.Get<RenderDegradeState>(entry.Entity),
            _world.Get<RenderSortKey>(entry.Entity),
            _world.Get<RenderDirtyMask>(entry.Entity),
            _world.Get<RenderSourceMetadata>(entry.Entity),
            _world.Get<RenderEntityPayload>(entry.Entity));

    private void Destroy(in SceneEntry entry)
    {
        RenderProjectionRecord record = ReadRecord(in entry);
        _directionalShadowPartPoses?.Remove(record.Id);
        RemoveFromIndices(in record);
        _world.Destroy(entry.Entity);
        DecrementCount(entry.ProjectionClass);
    }

    private void OrDirty(
        Entity entity,
        RenderProjectionId id,
        RenderDirtyMask value)
    {
        ref RenderDirtyMask dirty = ref _world.Get<RenderDirtyMask>(entity);
        dirty |= value;
        _dirty.Add(id);
    }

    private void UpdateIndices(
        in RenderProjectionRecord prior,
        in RenderProjectionRecord current)
    {
        if (IndexMembershipEquals(in prior, in current))
        {
            if (!DirectionalShadowTopologyEquals(in prior, in current))
                AdvanceDirectionalShadowTopologyRevision();
            SynchronizeDirtyIndex(in current);
            return;
        }

        RemoveFromIndices(in prior);
        AddToIndices(in current);
    }

    private void AddToIndices(in RenderProjectionRecord record)
    {
        bool dynamic = IsDynamic(record.ProjectionClass);
        bool staticProjection = !dynamic;
        bool indoor = InteriorEntityPartition.IsIndoorCellId(
            record.Source.ParentCellId == 0
                ? null
                : record.Source.ParentCellId);

        if (staticProjection)
        {
            if (indoor)
                _indoorCellStatics.Add(record.Id);
            else
                _outdoorStatics.Add(record.Id);
        }
        else
        {
            _dynamics.Add(record.Id);
            if (!indoor)
                _outdoorDynamics.Add(record.Id);
            if ((record.Flags & RenderProjectionFlags.PortalStraddling) != 0)
                _portalStraddlingDynamics.Add(record.Id);
        }

        if ((record.Flags & RenderProjectionFlags.Translucent) != 0)
            _translucent.Add(record.Id);
        if ((record.Flags & RenderProjectionFlags.Selectable) != 0)
            _selectable.Add(record.Id);
        if ((record.Flags & RenderProjectionFlags.LightCandidate) != 0)
            _lightCandidates.Add(record.Id);
        if (record.DirtyMask != RenderDirtyMask.None)
            _dirty.Add(record.Id);
        if (record.Source.LocalEntityId != 0 && !IsEnvCellShell(in record))
            _byLocalEntityId[record.Source.LocalEntityId] = record.Id;
        AdvanceIndexRevision();
        AdvanceDirectionalShadowTopologyRevision();
    }

    private void RemoveFromIndices(in RenderProjectionRecord record)
    {
        _outdoorStatics.Remove(record.Id);
        _indoorCellStatics.Remove(record.Id);
        _dynamics.Remove(record.Id);
        _outdoorDynamics.Remove(record.Id);
        _portalStraddlingDynamics.Remove(record.Id);
        _translucent.Remove(record.Id);
        _selectable.Remove(record.Id);
        _lightCandidates.Remove(record.Id);
        _dirty.Remove(record.Id);
        if (record.Source.LocalEntityId != 0
            && _byLocalEntityId.TryGetValue(
                record.Source.LocalEntityId,
                out RenderProjectionId mapped)
            && mapped == record.Id)
        {
            _byLocalEntityId.Remove(record.Source.LocalEntityId);
        }
        AdvanceIndexRevision();
        AdvanceDirectionalShadowTopologyRevision();
    }

    private static bool IsEnvCellShell(in RenderProjectionRecord record) =>
        record.ProjectionClass == RenderProjectionClass.IndoorCellStatic
        && record.EntityPayload.MeshRefs is null;

    private static bool IndexMembershipEquals(
        in RenderProjectionRecord left,
        in RenderProjectionRecord right)
    {
        const RenderProjectionFlags indexedFlags =
            RenderProjectionFlags.Translucent
            | RenderProjectionFlags.Selectable
            | RenderProjectionFlags.LightCandidate
            | RenderProjectionFlags.PortalStraddling
            | RenderProjectionFlags.SpatiallyResident;

        return left.ProjectionClass == right.ProjectionClass
            // _byLocalEntityId is keyed by this field (S2 review F6).
            && left.Source.LocalEntityId == right.Source.LocalEntityId
            && left.Source.ParentCellId == right.Source.ParentCellId
            && left.Residency.FullCellId == right.Residency.FullCellId
            && (left.Flags & indexedFlags) == (right.Flags & indexedFlags)
            && left.MeshSet.MeshCount == right.MeshSet.MeshCount
            && left.SortKey == right.SortKey;
    }

    private static bool DirectionalShadowTopologyEquals(
        in RenderProjectionRecord left,
        in RenderProjectionRecord right)
    {
        const RenderProjectionFlags eligibilityFlags =
            RenderProjectionFlags.Draw
            | RenderProjectionFlags.SpatiallyResident
            | RenderProjectionFlags.Translucent;

        if (left.ProjectionClass != right.ProjectionClass
            || left.OwnerIncarnation != right.OwnerIncarnation
            || left.Source.ParentCellId != right.Source.ParentCellId
            || (left.Flags & eligibilityFlags) != (right.Flags & eligibilityFlags)
            || left.SortKey != right.SortKey
            || left.MeshSet.MeshCount != right.MeshSet.MeshCount
            || left.Material != right.Material
            || left.DegradeState != right.DegradeState
            || left.Source.AppearanceFingerprint
                != right.Source.AppearanceFingerprint
            || left.Source.DirectionalShadowTopologyFingerprint
                != right.Source.DirectionalShadowTopologyFingerprint
            || left.EntityPayload.IsBuildingShell
                != right.EntityPayload.IsBuildingShell
            || left.EntityPayload.CasterIdentity
                != right.EntityPayload.CasterIdentity
            || !PaletteEquals(
                left.EntityPayload.PaletteOverride,
                right.EntityPayload.PaletteOverride))
        {
            return false;
        }

        bool refreshableTransforms =
            HasRefreshableDirectionalShadowTransforms(left.ProjectionClass);
        if (!refreshableTransforms
            && (left.Transform != right.Transform
                || left.MeshSet != right.MeshSet
                || left.Source.GeometryFingerprint
                    != right.Source.GeometryFingerprint))
        {
            return false;
        }

        IReadOnlyList<AcDream.Core.World.MeshRef>? leftMeshes =
            left.EntityPayload.MeshRefs;
        IReadOnlyList<AcDream.Core.World.MeshRef>? rightMeshes =
            right.EntityPayload.MeshRefs;
        if (ReferenceEquals(leftMeshes, rightMeshes))
            return true;
        if (leftMeshes is null
            || rightMeshes is null
            || leftMeshes.Count != rightMeshes.Count)
        {
            return false;
        }

        for (int meshIndex = 0; meshIndex < leftMeshes.Count; meshIndex++)
        {
            AcDream.Core.World.MeshRef leftMesh = leftMeshes[meshIndex];
            AcDream.Core.World.MeshRef rightMesh = rightMeshes[meshIndex];
            if (leftMesh.GfxObjId != rightMesh.GfxObjId
                || !SurfaceOverridesEqual(
                    leftMesh.SurfaceOverrides,
                    rightMesh.SurfaceOverrides)
                || (!refreshableTransforms
                    && leftMesh.PartTransform != rightMesh.PartTransform))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRefreshableDirectionalShadowTransforms(
        RenderProjectionClass projectionClass) =>
        projectionClass is RenderProjectionClass.ActiveAnimatedStatic
            or RenderProjectionClass.LiveDynamicRoot
            or RenderProjectionClass.EquippedChild;

    private static bool TransformBitsEqual(
        in RenderTransform left,
        in RenderTransform right)
    {
        Matrix4x4 leftMatrix = left.LocalToWorld;
        Matrix4x4 rightMatrix = right.LocalToWorld;
        ReadOnlySpan<Matrix4x4> leftSpan = MemoryMarshal.CreateReadOnlySpan(
            in leftMatrix,
            1);
        ReadOnlySpan<Matrix4x4> rightSpan = MemoryMarshal.CreateReadOnlySpan(
            in rightMatrix,
            1);
        return MemoryMarshal.AsBytes(leftSpan).SequenceEqual(
            MemoryMarshal.AsBytes(rightSpan));
    }

    private void EnsureDirectionalShadowTransformJournal()
    {
        if (_directionalShadowTransformChanges is not null)
            return;
        _directionalShadowTransformChanges = new DirectionalShadowTransformChange[
            DirectionalShadowTransformChangeJournal.Capacity];
        _directionalShadowTransformRevision = 1;
        _directionalShadowTransformChangeCount = 0;
        _directionalShadowPartPoses = new Dictionary<
            RenderProjectionId,
            DirectionalShadowPartPoseSnapshot>();
        foreach (SceneEntry entry in _entries.Values)
        {
            if (!HasRefreshableDirectionalShadowTransforms(entry.ProjectionClass))
                continue;
            RenderProjectionRecord record = ReadRecord(in entry);
            SynchronizeDirectionalShadowPartPose(in record);
        }
    }

    private void PublishDirectionalShadowTransformChange(
        in RenderProjectionRecord projection,
        DirectionalShadowTransformChangeKind kind)
    {
        DirectionalShadowTransformChange[]? journal =
            _directionalShadowTransformChanges;
        if (journal is null)
            return;
        if (_directionalShadowTransformRevision == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Directional-shadow transform revision space was exhausted.");
        }
        ulong revision = ++_directionalShadowTransformRevision;
        journal[(int)(revision % (ulong)journal.Length)] =
            new DirectionalShadowTransformChange(
                revision,
                DirectionalShadowTransformSnapshot.Capture(in projection),
                kind);
        if (_directionalShadowTransformChangeCount < journal.Length)
            _directionalShadowTransformChangeCount++;
    }

    private void ResetDirectionalShadowTransformChanges()
    {
        if (_directionalShadowTransformChanges is null)
            return;
        _directionalShadowTransformRevision = 1;
        _directionalShadowTransformChangeCount = 0;
        _directionalShadowPartPoses!.Clear();
    }

    private void SynchronizeDirectionalShadowPartPose(
        in RenderProjectionRecord record)
    {
        Dictionary<RenderProjectionId, DirectionalShadowPartPoseSnapshot>?
            poses = _directionalShadowPartPoses;
        if (poses is null
            || !HasRefreshableDirectionalShadowTransforms(record.ProjectionClass))
        {
            return;
        }
        if (!poses.TryGetValue(record.Id, out DirectionalShadowPartPoseSnapshot? pose))
        {
            poses.Add(record.Id, DirectionalShadowPartPoseSnapshot.Capture(in record));
            return;
        }
        pose.CaptureCurrent(in record);
    }

    private void PublishDirectionalShadowPartPoseChangeIfNeeded(
        in RenderProjectionRecord record)
    {
        Dictionary<RenderProjectionId, DirectionalShadowPartPoseSnapshot>?
            poses = _directionalShadowPartPoses;
        if (poses is null)
            return;
        if (!poses.TryGetValue(record.Id, out DirectionalShadowPartPoseSnapshot? pose))
        {
            poses.Add(record.Id, DirectionalShadowPartPoseSnapshot.Capture(in record));
            return;
        }
        if (!pose.CaptureCurrent(in record))
            return;
        PublishDirectionalShadowTransformChange(
            in record,
            DirectionalShadowTransformChangeKind.UpdateAppearance);
    }

    private static bool PaletteEquals(
        AcDream.Core.World.PaletteOverride? left,
        AcDream.Core.World.PaletteOverride? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null
            || right is null
            || left.BasePaletteId != right.BasePaletteId
            || left.SubPalettes.Count != right.SubPalettes.Count)
        {
            return false;
        }

        for (int index = 0; index < left.SubPalettes.Count; index++)
        {
            if (left.SubPalettes[index] != right.SubPalettes[index])
                return false;
        }

        return true;
    }

    private static bool SurfaceOverridesEqual(
        IReadOnlyDictionary<uint, uint>? left,
        IReadOnlyDictionary<uint, uint>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        foreach ((uint surfaceId, uint textureId) in left)
        {
            if (!right.TryGetValue(surfaceId, out uint candidate)
                || candidate != textureId)
            {
                return false;
            }
        }

        return true;
    }

    private void SynchronizeDirtyIndex(
        in RenderProjectionRecord record)
    {
        if (record.DirtyMask == RenderDirtyMask.None)
            _dirty.Remove(record.Id);
        else
            _dirty.Add(record.Id);
    }

    private void AdvanceIndexRevision()
    {
        if (_indexRevision == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Render-scene index revision space was exhausted.");
        }

        _indexRevision++;
    }

    private void AdvanceDirectionalShadowTopologyRevision()
    {
        if (_directionalShadowTopologyRevision == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Directional-shadow topology revision space was exhausted.");
        }

        _directionalShadowTopologyRevision++;
    }

    private static bool IsDynamic(RenderProjectionClass projectionClass) =>
        projectionClass is RenderProjectionClass.LiveDynamicRoot
            or RenderProjectionClass.EquippedChild;

    private HashSet<RenderProjectionId> Index(RenderSceneIndex index) =>
        index switch
        {
            RenderSceneIndex.OutdoorStatic => _outdoorStatics,
            RenderSceneIndex.IndoorCellStatic => _indoorCellStatics,
            RenderSceneIndex.Dynamic => _dynamics,
            RenderSceneIndex.OutdoorDynamic => _outdoorDynamics,
            RenderSceneIndex.PortalStraddlingDynamic =>
                _portalStraddlingDynamics,
            RenderSceneIndex.Translucent => _translucent,
            RenderSceneIndex.Selectable => _selectable,
            RenderSceneIndex.LightCandidate => _lightCandidates,
            RenderSceneIndex.Dirty => _dirty,
            _ => throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                null),
        };

    private int CopyIdsTo(
        HashSet<RenderProjectionId> source,
        Span<RenderProjectionRecord> destination)
    {
        if (destination.Length < source.Count)
        {
            throw new ArgumentException(
                $"Destination holds {destination.Length} records; {source.Count} required.",
                nameof(destination));
        }

        int count = 0;
        foreach (RenderProjectionId id in source)
        {
            if (!_entries.TryGetValue(id, out SceneEntry entry))
            {
                throw new InvalidOperationException(
                    $"Render index retained missing {id}.");
            }
            destination[count++] = ReadRecord(in entry);
        }
        return count;
    }

    private long EstimateIndexBytes()
    {
        long slots =
            _outdoorStatics.EnsureCapacity(0)
            + _indoorCellStatics.EnsureCapacity(0)
            + _dynamics.EnsureCapacity(0)
            + _outdoorDynamics.EnsureCapacity(0)
            + _portalStraddlingDynamics.EnsureCapacity(0)
            + _translucent.EnsureCapacity(0)
            + _selectable.EnsureCapacity(0)
            + _lightCandidates.EnsureCapacity(0)
            + _dirty.EnsureCapacity(0);
        long cellLookupSlots = _byLocalEntityId.EnsureCapacity(0);
        return checked(slots * 24L + cellLookupSlots * 40L);
    }

    private void ClearIndices()
    {
        _outdoorStatics.Clear();
        _indoorCellStatics.Clear();
        _dynamics.Clear();
        _outdoorDynamics.Clear();
        _portalStraddlingDynamics.Clear();
        _translucent.Clear();
        _selectable.Clear();
        _lightCandidates.Clear();
        _dirty.Clear();
        _byLocalEntityId.Clear();
    }

    private void IncrementCount(RenderProjectionClass projectionClass)
    {
        _counts = WithClassCount(
            _counts,
            projectionClass,
            _counts.For(projectionClass) + 1,
            _counts.Total + 1);
    }

    private void DecrementCount(RenderProjectionClass projectionClass)
    {
        _counts = WithClassCount(
            _counts,
            projectionClass,
            _counts.For(projectionClass) - 1,
            _counts.Total - 1);
    }

    private static RenderProjectionCounts WithClassCount(
        RenderProjectionCounts counts,
        RenderProjectionClass projectionClass,
        int classCount,
        int total) =>
        projectionClass switch
        {
            RenderProjectionClass.OutdoorStatic =>
                counts with { Total = total, OutdoorStatic = classCount },
            RenderProjectionClass.IndoorCellStatic =>
                counts with { Total = total, IndoorCellStatic = classCount },
            RenderProjectionClass.LiveDynamicRoot =>
                counts with { Total = total, LiveDynamicRoot = classCount },
            RenderProjectionClass.ActiveAnimatedStatic =>
                counts with { Total = total, ActiveAnimatedStatic = classCount },
            RenderProjectionClass.EquippedChild =>
                counts with { Total = total, EquippedChild = classCount },
            _ => throw new ArgumentOutOfRangeException(
                nameof(projectionClass),
                projectionClass,
                null),
        };

    private void EnsureMutationThread()
    {
        EnsureAvailable();
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "Render-scene mutation must remain on its owning update thread.");
        }
    }

    private void EnsureQueryGeneration(RenderSceneGeneration generation)
    {
        EnsureAvailable();
        if (generation != Generation)
        {
            throw new InvalidOperationException(
                $"Borrowed render-scene query belongs to {generation}; "
                + $"the current scene is {Generation}.");
        }
    }

    private void EnsureAvailable() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static void AddRecord(
        ref StableRenderHash128 hash,
        in RenderProjectionRecord record)
    {
        hash.Add(record.Id.RawValue);
        hash.Add((byte)record.ProjectionClass);
        hash.Add(record.OwnerIncarnation.RawValue);
        hash.Add(record.Transform.Position);
        hash.Add(record.Transform.Rotation);
        hash.Add(record.Transform.UniformScale);
        hash.Add(record.Transform.LocalToWorld);
        hash.Add(record.PreviousTransform.LocalToWorld);
        hash.Add(record.MeshSet.Handle.RawValue);
        hash.Add(record.MeshSet.MeshCount);
        hash.Add(record.MeshSet.Revision);
        hash.Add(record.Material.PaletteKey);
        hash.Add(record.Material.TextureReplacementKey);
        hash.Add(record.Material.Opacity);
        hash.Add(record.Residency.Bucket.RawValue);
        hash.Add(record.Residency.OwnerLandblockId);
        hash.Add(record.Residency.FullCellId);
        hash.Add(record.Bounds.Minimum);
        hash.Add(record.Bounds.Maximum);
        hash.Add((uint)record.Flags);
        hash.Add(record.DegradeState.Level);
        hash.Add(record.DegradeState.Revision);
        hash.Add(record.SortKey.Value);
        hash.Add(record.Source.LocalEntityId);
        hash.Add(record.Source.ServerGuid);
        hash.Add(record.Source.SourceId);
        hash.Add(record.Source.ParentCellId);
        hash.Add(record.Source.EffectCellId);
        hash.Add(record.Source.BuildingShellAnchorCellId);
        hash.Add(record.Source.TransformFingerprint.Low);
        hash.Add(record.Source.TransformFingerprint.High);
        hash.Add(record.Source.GeometryFingerprint.Low);
        hash.Add(record.Source.GeometryFingerprint.High);
        hash.Add(record.Source.AppearanceFingerprint.Low);
        hash.Add(record.Source.AppearanceFingerprint.High);
        hash.Add(record.Source.CurrentProjectionFlags);
        hash.Add((byte)record.EntityPayload.CasterIdentity);
    }

    private readonly record struct ProjectionIdentity(RenderProjectionId Id);

    private readonly record struct DirectionalShadowTransformChange(
        ulong Revision,
        DirectionalShadowTransformSnapshot Projection,
        DirectionalShadowTransformChangeKind Kind);

    private sealed class DirectionalShadowPartPoseSnapshot
    {
        private Matrix4x4[] _parts;

        private DirectionalShadowPartPoseSnapshot(Matrix4x4[] parts) =>
            _parts = parts;

        internal int Count => _parts.Length;

        internal static DirectionalShadowPartPoseSnapshot Capture(
            in RenderProjectionRecord record)
        {
            IReadOnlyList<AcDream.Core.World.MeshRef>? meshes =
                record.EntityPayload.MeshRefs;
            var parts = new Matrix4x4[meshes?.Count ?? 0];
            for (int index = 0; index < parts.Length; index++)
                parts[index] = meshes![index].PartTransform;
            return new DirectionalShadowPartPoseSnapshot(parts);
        }

        internal bool CaptureCurrent(in RenderProjectionRecord record)
        {
            IReadOnlyList<AcDream.Core.World.MeshRef>? meshes =
                record.EntityPayload.MeshRefs;
            int count = meshes?.Count ?? 0;
            bool changed = _parts.Length != count;
            if (changed)
                _parts = new Matrix4x4[count];
            for (int index = 0; index < count; index++)
            {
                Matrix4x4 current = meshes![index].PartTransform;
                if (!MatrixBitsEqual(in _parts[index], in current))
                {
                    _parts[index] = current;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool MatrixBitsEqual(
            in Matrix4x4 left,
            in Matrix4x4 right)
        {
            ReadOnlySpan<Matrix4x4> leftSpan = MemoryMarshal.CreateReadOnlySpan(
                in left,
                1);
            ReadOnlySpan<Matrix4x4> rightSpan = MemoryMarshal.CreateReadOnlySpan(
                in right,
                1);
            return MemoryMarshal.AsBytes(leftSpan).SequenceEqual(
                MemoryMarshal.AsBytes(rightSpan));
        }
    }

    private readonly record struct OutdoorStaticTag;

    private readonly record struct IndoorCellStaticTag;

    private readonly record struct LiveDynamicRootTag;

    private readonly record struct ActiveAnimatedStaticTag;

    private readonly record struct EquippedChildTag;

    private readonly record struct SceneEntry(
        Entity Entity,
        RenderOwnerIncarnation OwnerIncarnation,
        RenderProjectionClass ProjectionClass);

    private readonly record struct ProjectionLookupSlotEstimate(
        int HashCode,
        int Next,
        RenderProjectionId Key,
        SceneEntry Entry);

    private struct ApplyResultBuilder
    {
        public int Applied;
        public int Registered;
        public int Updated;
        public int Replaced;
        public int Unregistered;
        public int RejectedGeneration;
        public int RejectedOutOfOrderSequence;
        public int RejectedStaleIncarnation;
        public int RejectedMissing;

        public readonly RenderDeltaApplyResult Build() =>
            new(
                Applied,
                Registered,
                Updated,
                Replaced,
                Unregistered,
                RejectedGeneration,
                RejectedOutOfOrderSequence,
                RejectedStaleIncarnation,
                RejectedMissing);
    }

    private sealed class RenderProjectionRecordComparer :
        IComparer<RenderProjectionRecord>
    {
        public static RenderProjectionRecordComparer Instance { get; } = new();

        public int Compare(
            RenderProjectionRecord x,
            RenderProjectionRecord y)
        {
            int value = x.Id.CompareTo(y.Id);
            return value != 0
                ? value
                : x.ProjectionClass.CompareTo(y.ProjectionClass);
        }
    }
}
