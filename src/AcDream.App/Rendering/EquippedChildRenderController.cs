using System.Numerics;
using AcDream.App.World;
using AcDream.App.Rendering.Vfx;
using AcDream.Core.Items;
using AcDream.Core.Meshing;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using AcDream.Runtime.Entities;

namespace AcDream.App.Rendering;

public sealed class EquippedChildRenderController : IDisposable
{
    private readonly IDatReaderWriter _dats;
    private readonly object _datLock;
    private readonly ClientObjectTable _objects;
    private readonly LiveEntityRuntime _liveEntities;
    private readonly Func<ParentEvent.Parsed, bool> _acceptParent;
    private readonly Func<LiveEntityRecord, ulong, ulong, ExactProjectionWithdrawalOutcome>
        _withdrawProjection;
    private readonly EntityEffectPoseRegistry _poses;

    private readonly ShadowObjectRegistry _shadows;

    private readonly PhysicsDataCache _physicsData;

    private readonly Action<uint, GfxObj> _publishGfxObj;

    private ParentAttachmentState Relations => _liveEntities.ParentAttachments;

    /// <summary>Raised after the attached projection is fully registered.</summary>
    internal event Action<LiveEntityReadyCandidate>? EntityReady;
    public event Action<uint>? ProjectionPoseReady;
    public event Action<LiveEntityRecord>? ProjectionRemoved;
    private readonly Dictionary<RuntimeEntityKey, AttachedChild> _attachedByChild = [];
    private readonly Dictionary<RuntimeEntityKey, PendingUnparentTransition>
        _pendingUnparentByChild = [];
    private readonly Dictionary<RuntimeEntityKey, PendingOrdinaryRemoval>
        _pendingOrdinaryRemovalByRoot = [];
    private readonly Dictionary<RuntimeEntityKey, PendingProjectionSubtree>
        _pendingDetachedRemovalByChild = [];
    private readonly Dictionary<RuntimeEntityKey, PendingProjectionSubtree>
        _pendingReparentRemovalByChild = [];
    private readonly Dictionary<RuntimeEntityKey, PendingProjectionSubtree>
        _pendingPoseLossRemovalByChild = [];
    private readonly Dictionary<RuntimeEntityKey, PendingProjectionSubtree>
        _pendingOrphanRemovalByChild = [];
    private readonly List<uint> _pendingProjectionChildrenScratch = new();
    private readonly List<KeyValuePair<RuntimeEntityKey, PendingOrdinaryRemoval>>
        _pendingOrdinaryRemovalScratch = new();
    private readonly List<KeyValuePair<RuntimeEntityKey, PendingProjectionSubtree>>
        _pendingProjectionSubtreeScratch = new();
    private readonly List<KeyValuePair<RuntimeEntityKey, PendingUnparentTransition>>
        _pendingUnparentScratch = new();
    private readonly AttachmentUpdateOrder<RuntimeEntityKey, AttachedChild>
        _updateOrder = new();
    private readonly AttachmentUpdateOrder<uint, uint> _relationRecoveryOrder =
        new();
    private readonly Func<AttachedChild, RuntimeEntityKey?> _parentOfAttached;
    private readonly Func<RuntimeEntityKey, bool> _tickAttached;
    private readonly Func<RuntimeEntityKey, bool> _reconcileAttached;
    private int _activePoseCompositionVisits;
    private readonly HashSet<uint> _loggedUnaddressableParentRefusals = [];

    internal int LastFullPoseCompositionVisits { get; private set; }
    internal int LastReconcilePoseCompositionVisits { get; private set; }

    public IEnumerable<uint> AttachedEntityIds
    {
        get
        {
            foreach (AttachedChild child in _attachedByChild.Values)
                yield return child.Entity.Id;
        }
    }

    public EquippedChildRenderController(
        IDatReaderWriter dats,
        object datLock,
        ClientObjectTable objects,
        LiveEntityRuntime liveEntities,
        EntityEffectPoseRegistry poses,
        Func<ParentEvent.Parsed, bool> acceptParent,
        Func<LiveEntityRecord, ulong, ulong, ExactProjectionWithdrawalOutcome>
            withdrawProjection,
        ShadowObjectRegistry shadows,
        PhysicsDataCache physicsData,
        Action<uint, GfxObj> publishGfxObj)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _datLock = datLock ?? throw new ArgumentNullException(nameof(datLock));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _acceptParent = acceptParent ?? throw new ArgumentNullException(nameof(acceptParent));
        _withdrawProjection = withdrawProjection
            ?? throw new ArgumentNullException(nameof(withdrawProjection));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
        _physicsData = physicsData ?? throw new ArgumentNullException(nameof(physicsData));
        _publishGfxObj = publishGfxObj
            ?? throw new ArgumentNullException(nameof(publishGfxObj));
        _parentOfAttached = static child => child.ParentRecord.ProjectionKey;
        _tickAttached = TickChild;
        _reconcileAttached = ReconcileChild;

        _objects.ObjectMoved += OnObjectMoved;
        _objects.MoveRolledBack += OnMoveRolledBack;
        _objects.ObjectRemovalClassified += OnObjectRemovalClassified;
    }

    public void OnSpawn(WorldSession.EntitySpawn spawn)
    {
        lock (_datLock)
        {
            if (spawn.ParentGuid is { } parentGuid and not 0
                && spawn.ParentLocation is { } parentLocation)
            {
                uint placementId = spawn.PlacementId ?? 0u;
                AcceptLateBoundCreateObjectRelation(
                    parentGuid,
                    spawn.Guid,
                    parentLocation,
                    placementId,
                    spawn.PositionSequence);
            }

            ResolveAndTryRealize(spawn.Guid);

            RetryWaitingDescendants(spawn.Guid);
        }
    }

    internal bool TryApplyAttachedAppearance(
        LiveEntityRecord expectedRecord,
        ulong expectedObjDescAuthorityVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedRecord);
        lock (_datLock)
        {
            if (!_liveEntities.IsCurrentObjDescAuthority(
                    expectedRecord,
                    expectedObjDescAuthorityVersion)
                || expectedRecord.ProjectionKind is not
                    LiveEntityProjectionKind.Attached
                || !expectedRecord.IsSpatiallyProjected
                || expectedRecord.WorldEntity is not { } expectedEntity
                || !Relations.RestoreLastAccepted(expectedRecord.ServerGuid))
            {
                return false;
            }

            bool projected = ResolveAndTryRealize(expectedRecord.ServerGuid);
            if (projected)
            {
                RetryWaitingDescendants(expectedRecord.ServerGuid);
            }
            return projected
                && _liveEntities.IsCurrentObjDescAuthority(
                    expectedRecord,
                    expectedObjDescAuthorityVersion)
                && expectedRecord.ProjectionKind is
                    LiveEntityProjectionKind.Attached
                && expectedRecord.IsSpatiallyProjected
                && ReferenceEquals(expectedRecord.WorldEntity, expectedEntity);
        }
    }

    public void OnWorldEntityRegistered(uint guid)
    {
        lock (_datLock)
        {
            RetryWaitingDescendants(guid);
        }
    }

    /// <summary>
    /// Retries pose-withdrawn descendants only after their parent's new root
    /// and indexed parts have been published.
    /// </summary>
    public void OnPosePublished(uint guid)
    {
        lock (_datLock)
            RetryWaitingDescendants(guid);
    }

    public void OnParentEvent(ParentEvent.Parsed update)
    {
        lock (_datLock)
        {
            Relations.Enqueue(update);
            if (ResolveAndTryRealize(update.ChildGuid))
                RetryWaitingDescendants(update.ChildGuid);
        }
    }

    public void OnLogicalTeardown(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_datLock)
            TearDownRecordProjections(record);
    }

    private void OnObjectRemovalClassified(ClientObjectRemoval removal)
    {
        if (removal.Reason is not ClientObjectRemovalReason.Ordinary)
            return;

        uint guid = removal.Object.ObjectId;
        TearDownCurrentObjectProjections(guid);
        // InventoryRemoveObject removes the item from the UI view, not the
        // live physics generation. Preserve fresher pending ParentEvents so a
        // same-generation world/parent update can still replay them. Logical
        // delete/replacement are driven directly by the accepted inbound
        // lifecycle and never depend on this table.
    }

    public ChildUnparentDisposition OnChildBecameUnparented(
        uint childGuid,
        Action? continuation = null)
    {
        if (!_liveEntities.TryGetRecord(childGuid, out LiveEntityRecord record))
        {
            Relations.EndChildProjection(childGuid);
            return ChildUnparentDisposition.NotAttached;
        }

        RuntimeEntityKey key = RequireProjectionKey(record);
        var pending = new PendingUnparentTransition(
            record,
            record.PositionAuthorityVersion,
            CaptureProjectionSubtreeParentFirst(
                record,
                restoreRootRelation: false,
                restoreDescendantRelations: true),
            continuation);
        return AdvanceUnparentTransition(key, pending);
    }

    public void Tick()
    {
        RetryPendingProjectionTransitions();
        _activePoseCompositionVisits = 0;
        IReadOnlyList<RuntimeEntityKey> failed = _updateOrder.ForEachParentFirst(
            _attachedByChild,
            _parentOfAttached,
            _tickAttached);
        LastFullPoseCompositionVisits = _activePoseCompositionVisits;
        for (int i = 0; i < failed.Count; i++)
            WithdrawForPoseLoss(failed[i]);
    }

    public void ReconcileSpatialMutations()
    {
        RetryPendingProjectionTransitions();
        _activePoseCompositionVisits = 0;
        IReadOnlyList<RuntimeEntityKey> failed = _updateOrder.ForEachParentFirst(
            _attachedByChild,
            _parentOfAttached,
            _reconcileAttached);
        LastReconcilePoseCompositionVisits = _activePoseCompositionVisits;
        for (int i = 0; i < failed.Count; i++)
            WithdrawForPoseLoss(failed[i]);
    }

    private void RetryPendingProjectionTransitions()
    {
        CopyEntries(
            _pendingOrdinaryRemovalByRoot,
            _pendingOrdinaryRemovalScratch);
        for (int i = 0; i < _pendingOrdinaryRemovalScratch.Count; i++)
        {
            (RuntimeEntityKey rootKey, PendingOrdinaryRemoval pending) =
                _pendingOrdinaryRemovalScratch[i];
            AdvanceOrdinaryRemoval(rootKey, pending);
        }

        CopyEntries(
            _pendingDetachedRemovalByChild,
            _pendingProjectionSubtreeScratch);
        for (int i = 0; i < _pendingProjectionSubtreeScratch.Count; i++)
        {
            (RuntimeEntityKey childKey, PendingProjectionSubtree pending) =
                _pendingProjectionSubtreeScratch[i];
            AdvanceDetachedRemoval(childKey, pending);
        }

        RetryProjectionSubtrees(_pendingReparentRemovalByChild);
        RetryProjectionSubtrees(_pendingPoseLossRemovalByChild);
        RetryProjectionSubtrees(_pendingOrphanRemovalByChild);

        CopyEntries(_pendingUnparentByChild, _pendingUnparentScratch);
        for (int i = 0; i < _pendingUnparentScratch.Count; i++)
        {
            (RuntimeEntityKey childKey, PendingUnparentTransition pending) =
                _pendingUnparentScratch[i];
            if (!_liveEntities.IsCurrentRecord(pending.Record)
                || pending.Record.PositionAuthorityVersion
                    != pending.PositionAuthorityVersion)
            {
                _pendingUnparentByChild.Remove(childKey);
                continue;
            }
            AdvanceUnparentTransition(childKey, pending);
        }

        Relations.CopyPendingProjectionChildrenTo(_pendingProjectionChildrenScratch);
        for (int i = 0; i < _pendingProjectionChildrenScratch.Count; i++)
            ResolveAndTryRealize(_pendingProjectionChildrenScratch[i]);
    }

    private static void CopyEntries<T>(
        Dictionary<RuntimeEntityKey, T> source,
        List<KeyValuePair<RuntimeEntityKey, T>> destination)
    {
        destination.Clear();
        foreach (KeyValuePair<RuntimeEntityKey, T> entry in source)
            destination.Add(entry);
    }

    private bool TickChild(RuntimeEntityKey childKey)
    {
        if (!_attachedByChild.TryGetValue(childKey, out AttachedChild? child))
            return false;

        _activePoseCompositionVisits++;
        if (TryResolveExactAttachment(child, out WorldEntity parent)
            && _poses.TryGetRootPose(parent.Id, out Matrix4x4 parentWorld)
            && _poses.TryGetPartPoseSnapshot(
                parent.Id,
                out var parentPartPoses,
                out var parentPartAvailability)
            && EquippedChildAttachment.TryComposePoseInto(
                child.ParentSetup,
                parentPartPoses,
                parentPartAvailability,
                child.ChildSetup,
                child.ParentLocation,
                child.Placement,
                child.PartTemplate,
                child.Scale,
                child.PartPoseBuffer,
                child.AttachedPartBuffer,
                out EquippedChildPose pose))
        {
            child.Entity.MeshRefs = pose.AttachedParts;
            child.Entity.SetIndexedPartPoses(pose.PartLocal, child.PartAvailability);
            if (!ApplyParentWorldPose(child.Entity, parentWorld))
                return false;
            ApplyParentDrawVisibility(child.Entity, parent);
            child.Entity.ParentCellId = parent.ParentCellId;
            CaptureParentPresentation(child, parent);
            PublishChildPose(child.Entity, parentWorld, parent.ParentCellId, pose);
            if (TryResolveExactAttachment(child, out parent)
                && parent.ParentCellId is { } parentCellId)
            {
                EquippedChildPresentationRebucketDisposition disposition =
                    _liveEntities.RebucketEquippedChildPresentation(
                        child.ChildGuid,
                        parentCellId);
                if (disposition is
                    EquippedChildPresentationRebucketDisposition.NoProjection)
                {
                    return false;
                }
            }
            ProjectionPoseReady?.Invoke(child.ChildGuid);
            return true;
        }
        return false;
    }

    private bool ReconcileChild(RuntimeEntityKey childKey)
    {
        if (!_attachedByChild.TryGetValue(childKey, out AttachedChild? child)
            || !TryResolveExactAttachment(child, out WorldEntity parent))
        {
            return false;
        }

        return ParentPresentationMatches(child, parent) || TickChild(childKey);
    }

    private bool ParentPresentationMatches(
        AttachedChild child,
        WorldEntity parent)
        => ReferenceEquals(child.LastParentEntity, parent)
           && child.LastParentPoseVersion
                == _poses.GetPoseChangeVersion(parent.Id)
           && child.LastParentDrawVisible == parent.IsDrawVisible
           && child.LastParentAncestorDrawVisible
                == parent.IsAncestorDrawVisible
           && child.LastParentCellId == parent.ParentCellId;

    private void CaptureParentPresentation(
        AttachedChild child,
        WorldEntity parent)
    {
        child.LastParentEntity = parent;
        child.LastParentPoseVersion = _poses.GetPoseChangeVersion(parent.Id);
        child.LastParentDrawVisible = parent.IsDrawVisible;
        child.LastParentAncestorDrawVisible = parent.IsAncestorDrawVisible;
        child.LastParentCellId = parent.ParentCellId;
    }

    private bool TryRealize(
        ParentAttachmentRelation pending,
        ParentProjectionCandidateKind candidateKind)
    {
        uint childGuid = pending.ChildGuid;

        if (!_liveEntities.TryGetRecord(pending.ParentGuid, out LiveEntityRecord parentRecord)
            || parentRecord.WorldEntity is not { } parentEntity
            || !parentRecord.IsSpatiallyProjected
            || !_liveEntities.TryGetCanonical(
                childGuid,
                out RuntimeEntityRecord childCanonical)
            || !_liveEntities.TryGetSnapshot(pending.ParentGuid, out WorldSession.EntitySpawn parentSpawn)
            || !_liveEntities.TryGetSnapshot(childGuid, out WorldSession.EntitySpawn childSpawn))
            return false;

        if (parentSpawn.SetupTableId is not { } parentSetupId
            || childSpawn.SetupTableId is not { } childSetupId
            || parentEntity.ParentCellId is not { } parentCellId)
            return false;

        Setup? parentSetup = _dats.Get<Setup>(parentSetupId);
        Setup? childSetup = _dats.Get<Setup>(childSetupId);
        if (parentSetup is null || childSetup is null)
            return false;

        var parentLocation = (ParentLocation)pending.ParentLocation;
        var placement = (Placement)pending.PlacementId;
        IReadOnlyList<MeshRef> template = BuildPartTemplate(childSetup, childSpawn);
        bool[] childPartAvailability = BuildPartAvailability(template);
        float scale = childSpawn.ObjScale is { } objScale && objScale > 0f
            ? objScale
            : 1.0f;
        if (!_poses.TryGetPartPoseSnapshot(
                parentEntity.Id,
                out var parentPartPoses,
                out var parentPartAvailability))
            return false;
        if (!_poses.TryGetRootPose(parentEntity.Id, out Matrix4x4 parentWorld)
            || !TryDecomposeWorldPose(parentWorld, out Vector3 parentPosition, out Quaternion parentRotation))
        {
            return false;
        }

        if (!EquippedChildAttachment.TryComposePoseInto(
                parentSetup,
                parentPartPoses,
                parentPartAvailability,
                childSetup,
                parentLocation,
                placement,
                template,
                scale,
                partPoseBuffer: null,
                attachedPartBuffer: null,
                out EquippedChildPose pose))
            return false;

        // A parented object may materialize here before the ordinary world
        // hydration path sees it. Install its logical effect profile before
        // MaterializeLiveEntity registers runtime resources, exactly as for a
        // top-level projection; rebucketing/reattachment must never recreate it.
        var effectProfile = childSpawn.Physics is { } physics
            ? Vfx.EntityEffectProfile.CreateLive(childSetup, physics)
            : Vfx.EntityEffectProfile.CreateDatStatic(childSetup);
        if (_liveEntities.TryGetProjection(
                childCanonical,
                out LiveEntityRecord? retainedChild)
            && !_liveEntities.TryGetEffectProfile(childGuid, out _))
        {
            _liveEntities.SetEffectProfile(childGuid, effectProfile);
        }

        if (!_liveEntities.IsCurrentRecord(parentRecord)
            || !_liveEntities.IsCurrentCanonical(childCanonical)
            || !Relations.IsPending(pending, candidateKind))
        {
            return false;
        }
        if (retainedChild is not null
            && _liveEntities.IsCurrentRecord(retainedChild))
        {
            _liveEntities.ConvertMaterializationResidenceToLegacyImmediate(
                retainedChild);
        }
        WorldEntity? entity = _liveEntities.MaterializeLiveEntity(
            childCanonical,
            parentCellId,
            localId =>
            {
                var created = new WorldEntity
                {
                    Id = localId,
                    ServerGuid = childGuid,
                    SourceGfxObjOrSetupId = childSetupId,
                    Position = parentPosition,
                    Rotation = parentRotation,
                    MeshRefs = pose.AttachedParts,
                    PaletteOverride = BuildPaletteOverride(childSpawn),
                    ParentCellId = parentCellId,
                };
                created.SetIndexedPartPoses(pose.PartLocal, childPartAvailability);
                return created;
            },
            LiveEntityProjectionKind.Attached,
            initializeProjection: record => record.EffectProfile = effectProfile,
            out LiveEntityRecord? childRecord);
        if (entity is null || childRecord is null)
            return false;
        if (!_liveEntities.IsCurrentRecord(parentRecord)
            || !_liveEntities.IsCurrentRecord(childRecord)
            || !ReferenceEquals(childRecord.WorldEntity, entity)
            || !Relations.IsPending(pending, candidateKind))
        {
            if (_liveEntities.IsCurrentRecord(childRecord)
                && ReferenceEquals(childRecord.WorldEntity, entity)
                && childRecord.IsSpatiallyProjected)
            {
                BeginProjectionSubtreeWithdrawal(
                    _pendingOrphanRemovalByChild,
                    childRecord,
                    restoreRootRelation: false,
                    restoreDescendantRelations: false);
            }
            return false;
        }
        childRecord.HasPartArray = true;
        ApplyParentWorldPose(entity, parentWorld);
        ApplyParentDrawVisibility(entity, parentEntity);
        entity.ParentCellId = parentCellId;
        entity.ApplyAppearance(
            pose.AttachedParts,
            BuildPaletteOverride(childSpawn),
            childSpawn.AnimPartChanges is { Count: > 0 } changes
                ? changes.Select(change => new PartOverride(change.PartIndex, change.NewModelId)).ToArray()
                : Array.Empty<PartOverride>());
        entity.SetIndexedPartPoses(pose.PartLocal, childPartAvailability);
        var attached = new AttachedChild(
            parentRecord,
            childRecord,
            pending.ParentGuid,
            childGuid,
            parentLocation,
            placement,
            parentSetup,
            childSetup,
            template,
            childPartAvailability,
            pose.PartLocal,
            pose.AttachedParts,
            scale,
            entity);
        CaptureParentPresentation(attached, parentEntity);
        RuntimeEntityKey childKey = RequireProjectionKey(childRecord);
        _attachedByChild[childKey] = attached;
        _shadows.AttachChild(
            entity.Id,
            parentEntity.Id,
            BuildChildRenderParts(childSetup, template, scale));
        _pendingUnparentByChild.Remove(childKey);
        if ((parentRecord.FinalPhysicsState & PhysicsStateFlags.Hidden) != 0)
        {
            _liveEntities.SetAttachedChildNoDraw(childGuid, noDraw: true);
        }
        PublishChildPose(entity, parentWorld, parentEntity.ParentCellId, pose);
        Console.WriteLine(
            $"equipment: attached child=0x{childGuid:X8} parent=0x{pending.ParentGuid:X8} " +
            $"location={parentLocation} placement={placement}");
        Relations.MarkProjected(pending, candidateKind);
        LiveEntityReadyCandidate readyCandidate =
            LiveEntityReadyCandidate.Capture(childRecord);
        ProjectionPoseReady?.Invoke(childGuid);
        return PublishEntityReadyExact(
            _liveEntities,
            readyCandidate,
            EntityReady);
    }

    internal static bool PublishEntityReadyExact(
        LiveEntityRuntime runtime,
        LiveEntityReadyCandidate candidate,
        Action<LiveEntityReadyCandidate>? publish)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!candidate.IsCurrent(runtime))
            return false;

        publish?.Invoke(candidate);
        return candidate.IsCurrent(runtime);
    }

    private void PublishChildPose(
        WorldEntity child,
        Matrix4x4 parentWorld,
        uint? parentCellId,
        EquippedChildPose pose)
    {
        _poses.Publish(
            child.Id,
            pose.RootLocal * parentWorld,
            pose.PartLocal,
            parentCellId ?? 0u,
            child.IndexedPartAvailable);
    }

    internal static bool ApplyParentWorldPose(WorldEntity child, Matrix4x4 parentWorld)
    {
        if (!TryDecomposeWorldPose(parentWorld, out Vector3 position, out Quaternion rotation))
            return false;
        child.SetPosition(position);
        child.Rotation = rotation;
        return true;
    }

    internal static void ApplyParentDrawVisibility(WorldEntity child, WorldEntity parent)
    {
        child.IsAncestorDrawVisible =
            parent.IsDrawVisible && parent.IsAncestorDrawVisible;
    }

    private static bool TryDecomposeWorldPose(
        Matrix4x4 world,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = world.Translation;
        if (!Matrix4x4.Decompose(world, out Vector3 scale, out rotation, out _)
            || Vector3.DistanceSquared(scale, Vector3.One) > 1e-6f)
            return false;
        rotation = Quaternion.Normalize(rotation);
        return true;
    }

    public uint? FindChildLocalIdAtPart(uint parentLocalId, uint partIndex)
    {
        foreach (AttachedChild child in _attachedByChild.Values)
        {
            if (!TryResolveExactAttachment(child, out WorldEntity parent)
                || parent.Id != parentLocalId
                || !child.ParentSetup.HoldingLocations.TryGetValue(
                    child.ParentLocation,
                    out LocationType? holding)
                || holding.PartId != partIndex)
            {
                continue;
            }
            return child.Entity.Id;
        }
        return null;
    }

    public uint? FindParentLocalId(uint childLocalId)
    {
        foreach (AttachedChild child in _attachedByChild.Values)
        {
            if (child.Entity.Id != childLocalId)
                continue;
            if (TryResolveExactAttachment(child, out WorldEntity parent))
                return parent.Id;
            return null;
        }
        return null;
    }

    public void SetDirectChildrenNoDraw(uint parentGuid, bool noDraw)
    {
        foreach (AttachedChild child in _attachedByChild.Values)
        {
            if (child.ParentGuid == parentGuid
                && TryResolveExactAttachment(child, out _))
                _liveEntities.SetAttachedChildNoDraw(child.ChildGuid, noDraw);
        }
    }

    public void OnCreateParentAccepted(CreateParentUpdate update)
    {
        lock (_datLock)
        {
            AcceptLateBoundCreateObjectRelation(
                update.ParentGuid,
                update.ChildGuid,
                update.ParentLocation,
                update.PlacementId,
                update.ChildPositionSequence);
            ResolveAndTryRealize(update.ChildGuid);
        }
    }

    private void AcceptLateBoundCreateObjectRelation(
        uint parentGuid,
        uint childGuid,
        uint parentLocation,
        uint placementId,
        ushort childPositionSequence)
    {
        if (_liveEntities.TryGetSnapshot(parentGuid, out WorldSession.EntitySpawn parentSpawn))
        {
            Relations.AcceptCreateObjectRelation(new ParentAttachmentRelation(
                parentGuid,
                childGuid,
                parentLocation,
                placementId,
                parentSpawn.InstanceSequence,
                childPositionSequence));
        }
        else if (_loggedUnaddressableParentRefusals.Add(childGuid))
        {
            Console.Error.WriteLine(
                $"equipment: parent 0x{parentGuid:X8} unaddressable for child " +
                $"0x{childGuid:X8} at CreateObject-carried relation accept - " +
                "refusing (#319 A6; should be structurally unreachable - see " +
                "RuntimeEntityObjectLifetime.RegisterEntityCore's " +
                "EnqueueDeferredCreate gate). Logged once for this child; " +
                "further refusals for the same child are suppressed.");
        }
    }

    private bool TryResolveExactAttachment(
        AttachedChild child,
        out WorldEntity parent)
    {
        if (_liveEntities.IsCurrentRecord(child.ParentRecord)
            && _liveEntities.IsCurrentRecord(child.ChildRecord)
            && child.ParentRecord.IsSpatiallyProjected
            && child.ChildRecord.IsSpatiallyProjected
            && child.ParentRecord.WorldEntity is { } exactParent
            && child.ChildRecord.WorldEntity is { } exactChild
            && ReferenceEquals(exactChild, child.Entity))
        {
            parent = exactParent;
            return true;
        }
        parent = null!;
        return false;
    }

    private void ResolveRelations(uint childGuid)
    {
        Relations.Resolve(
            childGuid,
            guid => _liveEntities.TryGetSnapshot(guid, out _),
            ResolveLiveParentInstance,
            _acceptParent);
    }

    private ushort? ResolveLiveParentInstance(uint parentGuid) =>
        _liveEntities.TryGetSnapshot(parentGuid, out WorldSession.EntitySpawn spawn)
            ? spawn.InstanceSequence
            : null;

    private bool ResolveAndTryRealize(uint childGuid)
    {
        bool projected = false;
        while (true)
        {
            ResolveRelations(childGuid);
            if (!Relations.TryGetStagedProjection(
                    childGuid,
                    out ParentAttachmentRelation staged))
            {
                break;
            }

            ParentProjectionValidationDisposition validation =
                ValidateParentProjection(staged);
            if (validation is ParentProjectionValidationDisposition.Waiting)
                break;
            if (validation is ParentProjectionValidationDisposition.Rejected)
            {
                Relations.RejectProjection(staged);
                continue;
            }

            ProjectionPreparationResult result = PrepareAndTryRealize(
                staged,
                ParentProjectionCandidateKind.Staged);
            projected |= result.Projected;
            if (!result.CanAdvanceWireQueue)
                return projected;
        }

        if (Relations.TryGetRecoveryProjection(
                childGuid,
                out ParentAttachmentRelation recovery)
            && ValidateParentProjection(recovery)
                is ParentProjectionValidationDisposition.Ready)
        {
            projected |= PrepareAndTryRealize(
                recovery,
                ParentProjectionCandidateKind.Recovery).Projected;
        }
        return projected;
    }

    private ProjectionPreparationResult PrepareAndTryRealize(
        ParentAttachmentRelation relation,
        ParentProjectionCandidateKind candidateKind)
    {
        if (!_liveEntities.TryGetCanonical(
                relation.ChildGuid,
                out RuntimeEntityRecord childCanonical))
        {
            return default;
        }

        ulong positionAuthorityVersion =
            childCanonical.PositionAuthorityVersion;
        if (candidateKind is ParentProjectionCandidateKind.Staged)
        {
            if (!Relations.CanCommitIncarnation(relation, ResolveLiveParentInstance))
            {
                Relations.RejectProjection(relation);
                return new ProjectionPreparationResult(
                    CanAdvanceWireQueue: true,
                    Projected: false);
            }
            if (!_liveEntities.CommitStagedParent(relation, out _)
                || !Relations.CommitProjection(relation, ResolveLiveParentInstance))
            {
                return default;
            }
            candidateKind = ParentProjectionCandidateKind.Recovery;
        }
        else if (!Relations.IsCommitted(relation))
        {
            return default;
        }

        if (!Relations.IsPending(relation, candidateKind)
            || !_liveEntities.CommitAcceptedParentCellless(
                childCanonical,
                positionAuthorityVersion))
        {
            return default;
        }
        if (_liveEntities.TryGetProjection(
                childCanonical,
                out LiveEntityRecord? childRecord)
            && !WithdrawPriorProjection(childRecord))
        {
            return default;
        }
        return new(
            CanAdvanceWireQueue: true,
            Projected: TryRealize(relation, candidateKind));
    }

    internal ParentProjectionValidationDisposition ValidateParentProjection(
        ParentAttachmentRelation relation)
    {
        if (relation.ParentGuid == relation.ChildGuid)
            return ParentProjectionValidationDisposition.Rejected;
        if (!_liveEntities.TryGetRecord(relation.ParentGuid, out LiveEntityRecord parent)
            || !_liveEntities.TryGetCanonical(relation.ChildGuid, out _)
            || parent.WorldEntity is null
            || !parent.HasPartArray)
        {
            return ParentProjectionValidationDisposition.Waiting;
        }
        if (!_liveEntities.TryGetSnapshot(
                relation.ParentGuid,
                out WorldSession.EntitySpawn parentSpawn)
            || parentSpawn.SetupTableId is not { } parentSetupId)
        {
            return ParentProjectionValidationDisposition.Rejected;
        }
        Setup? parentSetup = _dats.Get<Setup>(parentSetupId);
        return parentSetup is not null
            && parentSetup.HoldingLocations.ContainsKey(
                (ParentLocation)relation.ParentLocation)
                ? ParentProjectionValidationDisposition.Ready
                : ParentProjectionValidationDisposition.Rejected;
    }

    private readonly record struct ProjectionPreparationResult(
        bool CanAdvanceWireQueue,
        bool Projected);

    private void RetryWaitingDescendants(uint parentGuid)
    {
        _relationRecoveryOrder.RealizeDescendants(
            parentGuid,
            Relations.ChildrenWaitingForParent,
            ResolveAndTryRealize);
    }

    private IReadOnlyList<MeshRef> BuildPartTemplate(
        Setup setup,
        WorldSession.EntitySpawn spawn)
    {
        var result = new MeshRef[setup.Parts.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new MeshRef((uint)setup.Parts[i], Matrix4x4.Identity);

        IReadOnlyList<CreateObject.AnimPartChange> partChanges =
            spawn.AnimPartChanges ?? Array.Empty<CreateObject.AnimPartChange>();
        for (int i = 0; i < partChanges.Count; i++)
        {
            CreateObject.AnimPartChange change = partChanges[i];
            if (change.PartIndex < result.Length)
                result[change.PartIndex] = new MeshRef(change.NewModelId, Matrix4x4.Identity);
        }

        IReadOnlyList<CreateObject.TextureChange> textureChanges =
            spawn.TextureChanges ?? Array.Empty<CreateObject.TextureChange>();
        for (int partIndex = 0; partIndex < result.Length; partIndex++)
        {
            Dictionary<uint, uint>? oldToNew = null;
            for (int t = 0; t < textureChanges.Count; t++)
            {
                CreateObject.TextureChange change = textureChanges[t];
                if (change.PartIndex != partIndex) continue;
                oldToNew ??= new Dictionary<uint, uint>();
                oldToNew[change.OldTexture] = change.NewTexture;
            }
            if (oldToNew is null) continue;

            GfxObj? gfx = _dats.Get<GfxObj>(result[partIndex].GfxObjId);
            if (gfx is null) continue;
            Dictionary<uint, uint>? surfaceOverrides = null;
            foreach (var surfaceQid in gfx.Surfaces)
            {
                uint surfaceId = (uint)surfaceQid;
                Surface? surface = _dats.Get<Surface>(surfaceId);
                if (surface is null) continue;
                uint oldTexture = (uint)surface.OrigTextureId;
                if (!oldToNew.TryGetValue(oldTexture, out uint replacement)) continue;
                surfaceOverrides ??= new Dictionary<uint, uint>();
                surfaceOverrides[surfaceId] = replacement;
            }

            if (surfaceOverrides is not null)
            {
                result[partIndex] = new MeshRef(
                    result[partIndex].GfxObjId,
                    Matrix4x4.Identity)
                {
                    SurfaceOverrides = surfaceOverrides,
                };
            }
        }

        return result;
    }

    private bool[] BuildPartAvailability(IReadOnlyList<MeshRef> template)
    {
        var available = new bool[template.Count];
        for (int i = 0; i < template.Count; i++)
        {
            uint gfxObjId = template[i].GfxObjId;
            GfxObj? gfxObj = _dats.Get<GfxObj>(gfxObjId);
            if (gfxObj is null)
                continue;

            _publishGfxObj(gfxObjId, gfxObj);
            available[i] = true;
        }
        return available;
    }

    private IReadOnlyList<ShadowShape> BuildChildRenderParts(
        Setup setup, IReadOnlyList<MeshRef> template, float scale)
    {
        var effectiveGfxObjIds = new uint[template.Count];
        for (int i = 0; i < template.Count; i++)
            effectiveGfxObjIds[i] = template[i].GfxObjId;
        return ShadowShapeBuilder.FromSetupRenderParts(
            setup,
            scale,
            effectiveGfxObjIds,
            partPoseOverride: null,
            _physicsData.GetGfxObj,
            _physicsData.GetVisualBounds);
    }

    private static PaletteOverride? BuildPaletteOverride(WorldSession.EntitySpawn spawn)
    {
        if (spawn.SubPalettes is not { Count: > 0 } subPalettes)
            return null;

        var ranges = new PaletteOverride.SubPaletteRange[subPalettes.Count];
        for (int i = 0; i < subPalettes.Count; i++)
        {
            CreateObject.SubPaletteSwap swap = subPalettes[i];
            ranges[i] = new PaletteOverride.SubPaletteRange(
                swap.SubPaletteId,
                swap.Offset,
                swap.Length);
        }
        return new PaletteOverride(spawn.BasePaletteId ?? 0, ranges);
    }

    private void OnObjectMoved(ClientObjectMove move)
    {
        if (move.Previous.EquipLocation != EquipMask.None
            && move.Current.EquipLocation == EquipMask.None)
            BeginDetachedRemoval(move.ItemId);
    }

    private void BeginDetachedRemoval(uint childGuid)
    {
        if (!_liveEntities.TryGetRecord(childGuid, out LiveEntityRecord record))
            return;
        BeginProjectionSubtreeWithdrawal(
            _pendingDetachedRemovalByChild,
            record,
            restoreRootRelation: false,
            restoreDescendantRelations: true);
    }

    private void AdvanceDetachedRemoval(
        RuntimeEntityKey childKey,
        PendingProjectionSubtree pending)
    {
        AdvanceProjectionSubtree(
            _pendingDetachedRemovalByChild,
            childKey,
            pending);
    }

    private void OnMoveRolledBack(ClientObject item)
    {
        if (item.CurrentlyEquippedLocation == EquipMask.None)
            return;
        if (!_liveEntities.TryGetRecord(
                item.ObjectId,
                out LiveEntityRecord record)
            || record.ProjectionKey is not { } key)
        {
            return;
        }
        if (_attachedByChild.TryGetValue(key, out AttachedChild? attached)
            && _liveEntities.IsCurrentRecord(attached.ChildRecord)
            && attached.ChildRecord.IsSpatiallyProjected)
        {
            _pendingDetachedRemovalByChild.Remove(key);
            return;
        }
        if (_pendingDetachedRemovalByChild.TryGetValue(
                key,
                out PendingProjectionSubtree pending))
        {
            AdvanceDetachedRemoval(key, pending);
            if (_pendingDetachedRemovalByChild.ContainsKey(key))
                return;
        }

        // A rejected unwield restores the equipped location without a fresh
        // wire ParentEvent; reinstall the last accepted relationship only for
        // this explicit rollback signal.
        if (Relations.RestoreLastAccepted(item.ObjectId))
        {
            lock (_datLock)
            {
                if (ResolveAndTryRealize(item.ObjectId))
                    RetryWaitingDescendants(item.ObjectId);
            }
        }
    }

    private bool Remove(uint childGuid)
    {
        if (!_liveEntities.TryGetRecord(
                childGuid,
                out LiveEntityRecord record)
            || record.ProjectionKey is not { } key
            || !_attachedByChild.TryGetValue(key, out AttachedChild? child))
            return false;
        if (!_liveEntities.IsCurrentRecord(child.ChildRecord))
            return CommitProjectionRemoval(child);
        return Remove(
            child,
            child.ChildRecord.PositionAuthorityVersion,
            child.ChildRecord.ProjectionMutationVersion);
    }

    private bool Remove(
        AttachedChild child,
        ulong positionAuthorityVersion,
        ulong projectionMutationVersion)
    {
        if (!_liveEntities.IsCurrentRecord(child.ChildRecord))
            return CommitProjectionRemoval(child);
        ExactProjectionWithdrawalOutcome outcome = WithdrawAttachedProjection(
            child.ChildRecord,
            positionAuthorityVersion,
            projectionMutationVersion,
            _withdrawProjection,
            () => CommitProjectionRemoval(child));
        if (outcome.Failure is not null)
            throw outcome.Failure;
        return outcome.Disposition is ExactProjectionWithdrawalDisposition.Completed;
    }

    private bool WithdrawPriorProjection(LiveEntityRecord childRecord)
    {
        RuntimeEntityKey key = RequireProjectionKey(childRecord);
        if (_pendingReparentRemovalByChild.TryGetValue(
                key,
                out PendingProjectionSubtree pending))
        {
            return AdvanceProjectionSubtree(
                _pendingReparentRemovalByChild,
                key,
                pending);
        }
        return BeginProjectionSubtreeWithdrawal(
            _pendingReparentRemovalByChild,
            childRecord,
            restoreRootRelation: false,
            restoreDescendantRelations: true);
    }

    private ChildUnparentDisposition AdvanceUnparentTransition(
        RuntimeEntityKey childKey,
        PendingUnparentTransition pending)
    {
        ExactProjectionWithdrawalOutcome outcome = AdvanceProjectionSubtree(
            pending.Subtree,
            out PendingProjectionSubtree next);

        if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Pending)
        {
            _pendingUnparentByChild[childKey] = pending with { Subtree = next };
            if (outcome.Failure is not null)
                throw outcome.Failure;
            return ChildUnparentDisposition.Pending;
        }

        if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Superseded)
        {
            _pendingUnparentByChild.Remove(childKey);
            if (outcome.Failure is not null)
                throw outcome.Failure;
            return ChildUnparentDisposition.Superseded;
        }

        PendingUnparentTransition awaitingContinuation = pending with { Subtree = next };
        _pendingUnparentByChild[childKey] = awaitingContinuation;
        if (outcome.Failure is not null)
            throw outcome.Failure;

        Relations.EndChildProjection(pending.Record.ServerGuid);
        try
        {
            awaitingContinuation.Continuation?.Invoke();
            _pendingUnparentByChild.Remove(childKey);
            return ChildUnparentDisposition.Completed;
        }
        catch
        {
            bool continuationCommitted =
                _liveEntities.IsCurrentRecord(awaitingContinuation.Record)
                && awaitingContinuation.Record.PositionAuthorityVersion
                    == awaitingContinuation.PositionAuthorityVersion
                && awaitingContinuation.Record.IsSpatiallyProjected
                && awaitingContinuation.Record.ProjectionKind
                    is LiveEntityProjectionKind.World;
            if (continuationCommitted)
                _pendingUnparentByChild.Remove(childKey);
            else
                _pendingUnparentByChild[childKey] = awaitingContinuation;
            throw;
        }
    }

    internal static ExactProjectionWithdrawalOutcome WithdrawAttachedProjection(
        LiveEntityRecord childRecord,
        ulong positionAuthorityVersion,
        ulong projectionMutationVersion,
        Func<LiveEntityRecord, ulong, ulong, ExactProjectionWithdrawalOutcome>
            withdrawProjection,
        Func<bool> commitRemoval)
    {
        ArgumentNullException.ThrowIfNull(childRecord);
        ArgumentNullException.ThrowIfNull(withdrawProjection);
        ArgumentNullException.ThrowIfNull(commitRemoval);
        ExactProjectionWithdrawalOutcome outcome = withdrawProjection(
            childRecord,
            positionAuthorityVersion,
            projectionMutationVersion);
        if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Pending)
            return outcome;
        try
        {
            commitRemoval();
            return outcome;
        }
        catch (Exception error)
        {
            return new ExactProjectionWithdrawalOutcome(
                outcome.Disposition,
                outcome.Failure is null
                    ? error
                    : new AggregateException(outcome.Failure, error));
        }
    }

    private bool CommitProjectionRemoval(AttachedChild child)
    {
        RuntimeEntityKey key = RequireProjectionKey(child.ChildRecord);
        if (!_attachedByChild.TryGetValue(key, out AttachedChild? current)
            || !ReferenceEquals(current, child))
        {
            return false;
        }

        _attachedByChild.Remove(key);
        _shadows.DetachChild(child.Entity.Id);
        ProjectionRemoved?.Invoke(child.ChildRecord);
        return true;
    }

    private void WithdrawForPoseLoss(RuntimeEntityKey childKey)
    {
        if (!_liveEntities.TryGetRecord(
                childKey,
                out LiveEntityRecord record))
            return;
        BeginProjectionSubtreeWithdrawal(
            _pendingPoseLossRemovalByChild,
            record,
            restoreRootRelation: true,
            restoreDescendantRelations: true);
    }

    private void TearDownCurrentObjectProjections(uint guid)
    {
        if (_liveEntities.TryGetRecord(guid, out LiveEntityRecord record))
        {
            RuntimeEntityKey key = RequireProjectionKey(record);
            List<AttachedRemovalCapture> captures = CaptureRecordSubtreeParentFirst(record);
            AdvanceOrdinaryRemoval(
                key,
                new PendingOrdinaryRemoval(record, captures, NextIndex: 0));
            return;
        }

        AttachedChild? stale = _attachedByChild.Values.FirstOrDefault(
            candidate => candidate.ChildGuid == guid);
        if (stale is not null)
            CommitProjectionRemoval(stale);
        Relations.EndChildProjection(guid);
    }

    private void AdvanceOrdinaryRemoval(
        RuntimeEntityKey rootKey,
        PendingOrdinaryRemoval pending)
    {
        if (!_liveEntities.IsCurrentRecord(pending.RootRecord))
        {
            _pendingOrdinaryRemovalByRoot.Remove(rootKey);
            return;
        }

        for (int i = pending.NextIndex; i < pending.Captures.Count; i++)
        {
            AttachedRemovalCapture captured = pending.Captures[i];
            ExactProjectionWithdrawalOutcome outcome = WithdrawCaptured(captured);
            if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Pending)
            {
                _pendingOrdinaryRemovalByRoot[rootKey] = pending with { NextIndex = i };
                if (outcome.Failure is not null)
                    throw outcome.Failure;
                return;
            }

            if (!ReferenceEquals(captured.Attached.ChildRecord, pending.RootRecord)
                && outcome.Disposition is ExactProjectionWithdrawalDisposition.Completed)
            {
                Relations.RestoreLastAccepted(captured.Attached.ChildGuid);
            }

            if (outcome.Failure is not null)
            {
                _pendingOrdinaryRemovalByRoot[rootKey] = pending with
                {
                    NextIndex = i + 1,
                };
                throw outcome.Failure;
            }
        }

        _pendingOrdinaryRemovalByRoot.Remove(rootKey);
        Relations.EndChildProjection(pending.RootRecord.ServerGuid);
    }

    private void TearDownRecordProjections(LiveEntityRecord record)
    {
        RuntimeEntityKey key = RequireProjectionKey(record);
        if (_pendingUnparentByChild.TryGetValue(key, out var pending)
            && ReferenceEquals(pending.Record, record))
        {
            _pendingUnparentByChild.Remove(key);
        }
        if (_pendingOrdinaryRemovalByRoot.TryGetValue(
                key,
                out PendingOrdinaryRemoval ordinary)
            && ReferenceEquals(ordinary.RootRecord, record))
        {
            _pendingOrdinaryRemovalByRoot.Remove(key);
        }
        RemovePendingProjectionSubtree(_pendingDetachedRemovalByChild, record);
        RemovePendingProjectionSubtree(_pendingReparentRemovalByChild, record);
        RemovePendingProjectionSubtree(_pendingPoseLossRemovalByChild, record);
        RemovePendingProjectionSubtree(_pendingOrphanRemovalByChild, record);
        List<AttachedRemovalCapture> subtree = CaptureRecordSubtreeParentFirst(record);
        for (int i = 0; i < subtree.Count; i++)
        {
            AttachedRemovalCapture captured = subtree[i];
            AttachedChild child = captured.Attached;
            if (ReferenceEquals(child.ChildRecord, record))
            {
                CommitProjectionRemoval(child);
                continue;
            }
            ExactProjectionWithdrawalOutcome outcome = WithdrawCaptured(captured);
            if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Pending)
            {
                if (outcome.Failure is not null)
                    throw outcome.Failure;
                throw new InvalidOperationException(
                    $"Attached projection 0x{child.ChildGuid:X8} remains pending exact teardown of 0x{record.ServerGuid:X8}.");
            }
            if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Completed)
                Relations.RestoreLastAccepted(child.ChildGuid);
            if (outcome.Failure is not null)
                throw outcome.Failure;
        }
    }

    private List<AttachedRemovalCapture> CaptureRecordSubtreeParentFirst(
        LiveEntityRecord record)
    {
        var result = new List<AttachedRemovalCapture>();
        var visited = new HashSet<LiveEntityRecord>(ReferenceEqualityComparer.Instance);
        if (record.ProjectionKey is { } key
            && _attachedByChild.TryGetValue(key, out AttachedChild? root)
            && ReferenceEquals(root.ChildRecord, record))
        {
            result.Add(Capture(root));
        }
        CollectDescendantsParentFirst(record, result, visited);
        return result;
    }

    private void CollectDescendantsParentFirst(
        LiveEntityRecord parent,
        List<AttachedRemovalCapture> destination,
        HashSet<LiveEntityRecord> visited)
    {
        if (!visited.Add(parent))
            return;
        AttachedChild[] children = _attachedByChild.Values
            .Where(child => ReferenceEquals(child.ParentRecord, parent))
            .ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            destination.Add(Capture(children[i]));
            CollectDescendantsParentFirst(children[i].ChildRecord, destination, visited);
        }
    }

    private static AttachedRemovalCapture Capture(AttachedChild child) => new(
        child,
        child.ChildRecord.PositionAuthorityVersion,
        child.ChildRecord.ProjectionMutationVersion);

    private ExactProjectionWithdrawalOutcome WithdrawCaptured(
        AttachedRemovalCapture captured)
    {
        RuntimeEntityKey key = RequireProjectionKey(
            captured.Attached.ChildRecord);
        if (!_attachedByChild.TryGetValue(
                key,
                out AttachedChild? current)
            || !ReferenceEquals(current, captured.Attached))
        {
            return new ExactProjectionWithdrawalOutcome(
                ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        }
        return WithdrawAttachedProjection(
            captured.Attached.ChildRecord,
            captured.PositionAuthorityVersion,
            captured.ProjectionMutationVersion,
            _withdrawProjection,
            () => CommitProjectionRemoval(captured.Attached));
    }

    private PendingProjectionSubtree CaptureProjectionSubtreeParentFirst(
        LiveEntityRecord root,
        bool restoreRootRelation,
        bool restoreDescendantRelations)
    {
        var captures = new List<ProjectionRemovalCapture>();
        var visited = new HashSet<LiveEntityRecord>(ReferenceEqualityComparer.Instance);
        CaptureProjectionNode(root, isRoot: true, captures, visited);
        return new PendingProjectionSubtree(
            root,
            root.PositionAuthorityVersion,
            captures,
            NextIndex: 0,
            restoreRootRelation,
            restoreDescendantRelations);
    }

    private void CaptureProjectionNode(
        LiveEntityRecord record,
        bool isRoot,
        List<ProjectionRemovalCapture> destination,
        HashSet<LiveEntityRecord> visited)
    {
        if (!visited.Add(record))
            return;
        AttachedChild? attached = null;
        if (record.ProjectionKey is { } key)
            _attachedByChild.TryGetValue(key, out attached);
        if (attached is not null && !ReferenceEquals(attached.ChildRecord, record))
            attached = null;
        if (record.IsSpatiallyProjected || attached is not null)
        {
            destination.Add(new ProjectionRemovalCapture(
                record,
                record.PositionAuthorityVersion,
                record.ProjectionMutationVersion,
                attached,
                isRoot));
        }

        AttachedChild[] children = _attachedByChild.Values
            .Where(child => ReferenceEquals(child.ParentRecord, record))
            .ToArray();
        for (int i = 0; i < children.Length; i++)
        {
            CaptureProjectionNode(
                children[i].ChildRecord,
                isRoot: false,
                destination,
                visited);
        }
    }

    private bool BeginProjectionSubtreeWithdrawal(
        Dictionary<RuntimeEntityKey, PendingProjectionSubtree> pendingByRoot,
        LiveEntityRecord root,
        bool restoreRootRelation,
        bool restoreDescendantRelations)
    {
        PendingProjectionSubtree pending = CaptureProjectionSubtreeParentFirst(
            root,
            restoreRootRelation,
            restoreDescendantRelations);
        return AdvanceProjectionSubtree(
            pendingByRoot,
            RequireProjectionKey(root),
            pending);
    }

    private bool AdvanceProjectionSubtree(
        Dictionary<RuntimeEntityKey, PendingProjectionSubtree> pendingByRoot,
        RuntimeEntityKey rootKey,
        PendingProjectionSubtree pending)
    {
        ExactProjectionWithdrawalOutcome outcome = AdvanceProjectionSubtree(
            pending,
            out PendingProjectionSubtree next);
        bool retry = outcome.Disposition is ExactProjectionWithdrawalDisposition.Pending
            || (outcome.Failure is not null && next.NextIndex < next.Captures.Count);
        if (retry)
            pendingByRoot[rootKey] = next;
        else
            pendingByRoot.Remove(rootKey);
        if (outcome.Failure is not null)
            throw outcome.Failure;
        return outcome.Disposition is ExactProjectionWithdrawalDisposition.Completed
            && next.NextIndex >= next.Captures.Count;
    }

    private ExactProjectionWithdrawalOutcome AdvanceProjectionSubtree(
        PendingProjectionSubtree pending,
        out PendingProjectionSubtree next)
    {
        next = pending;
        if (!_liveEntities.IsCurrentRecord(pending.RootRecord)
            || pending.RootRecord.PositionAuthorityVersion
                != pending.RootPositionAuthorityVersion)
        {
            return new(
                ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        }

        for (int i = pending.NextIndex; i < pending.Captures.Count; i++)
        {
            ProjectionRemovalCapture captured = pending.Captures[i];
            ExactProjectionWithdrawalOutcome outcome = WithdrawProjectionCapture(captured);
            if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Pending)
            {
                next = pending with { NextIndex = i };
                return outcome;
            }
            if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Superseded
                && captured.IsRoot)
            {
                next = pending with { NextIndex = i + 1 };
                return outcome;
            }
            if (outcome.Disposition is ExactProjectionWithdrawalDisposition.Completed
                && captured.Attached is not null
                && (captured.IsRoot
                    ? pending.RestoreRootRelation
                    : pending.RestoreDescendantRelations))
            {
                Relations.RestoreLastAccepted(captured.Record.ServerGuid);
            }
            next = pending with { NextIndex = i + 1 };
            if (outcome.Failure is not null)
                return outcome;
        }

        return new(
            ExactProjectionWithdrawalDisposition.Completed,
            Failure: null);
    }

    private ExactProjectionWithdrawalOutcome WithdrawProjectionCapture(
        ProjectionRemovalCapture captured)
    {
        if (captured.Attached is { } attached)
        {
            return WithdrawCaptured(new AttachedRemovalCapture(
                attached,
                captured.PositionAuthorityVersion,
                captured.ProjectionMutationVersion));
        }
        if (!_liveEntities.IsCurrentRecord(captured.Record)
            || captured.Record.PositionAuthorityVersion
                != captured.PositionAuthorityVersion
            || captured.Record.ProjectionMutationVersion
                != captured.ProjectionMutationVersion)
        {
            return new(
                ExactProjectionWithdrawalDisposition.Superseded,
                Failure: null);
        }
        if (!captured.Record.IsSpatiallyProjected)
        {
            return new(
                ExactProjectionWithdrawalDisposition.Completed,
                Failure: null);
        }
        return _withdrawProjection(
            captured.Record,
            captured.PositionAuthorityVersion,
            captured.ProjectionMutationVersion);
    }

    private void RetryProjectionSubtrees(
        Dictionary<RuntimeEntityKey, PendingProjectionSubtree> pendingByRoot)
    {
        CopyEntries(pendingByRoot, _pendingProjectionSubtreeScratch);
        for (int i = 0; i < _pendingProjectionSubtreeScratch.Count; i++)
        {
            (RuntimeEntityKey rootKey, PendingProjectionSubtree pending) =
                _pendingProjectionSubtreeScratch[i];
            AdvanceProjectionSubtree(pendingByRoot, rootKey, pending);
        }
    }

    private static void RemovePendingProjectionSubtree(
        Dictionary<RuntimeEntityKey, PendingProjectionSubtree> pendingByRoot,
        LiveEntityRecord record)
    {
        if (record.ProjectionKey is { } key
            && pendingByRoot.TryGetValue(
                key,
                out PendingProjectionSubtree pending)
            && ReferenceEquals(pending.RootRecord, record))
        {
            pendingByRoot.Remove(key);
        }
    }

    public void Clear()
    {
        AttachedChild[] attached = _attachedByChild.Values.ToArray();
        for (int i = 0; i < attached.Length; i++)
            CommitProjectionRemoval(attached[i]);
        _pendingUnparentByChild.Clear();
        _pendingOrdinaryRemovalByRoot.Clear();
        _pendingDetachedRemovalByChild.Clear();
        _pendingReparentRemovalByChild.Clear();
        _pendingPoseLossRemovalByChild.Clear();
        _pendingOrphanRemovalByChild.Clear();
        _loggedUnaddressableParentRefusals.Clear();
        Relations.Clear();
    }

    public void Dispose()
    {
        Clear();
        _objects.ObjectMoved -= OnObjectMoved;
        _objects.MoveRolledBack -= OnMoveRolledBack;
        _objects.ObjectRemovalClassified -= OnObjectRemovalClassified;
    }

    private sealed record AttachedChild(
        LiveEntityRecord ParentRecord,
        LiveEntityRecord ChildRecord,
        uint ParentGuid,
        uint ChildGuid,
        ParentLocation ParentLocation,
        Placement Placement,
        Setup ParentSetup,
        Setup ChildSetup,
        IReadOnlyList<MeshRef> PartTemplate,
        IReadOnlyList<bool> PartAvailability,
        Matrix4x4[] PartPoseBuffer,
        MeshRef[] AttachedPartBuffer,
        float Scale,
        WorldEntity Entity)
    {
        public WorldEntity? LastParentEntity { get; set; }
        public ulong LastParentPoseVersion { get; set; }
        public bool LastParentDrawVisible { get; set; }
        public bool LastParentAncestorDrawVisible { get; set; }
        public uint? LastParentCellId { get; set; }
    }

    private readonly record struct PendingUnparentTransition(
        LiveEntityRecord Record,
        ulong PositionAuthorityVersion,
        PendingProjectionSubtree Subtree,
        Action? Continuation);

    private readonly record struct PendingProjectionSubtree(
        LiveEntityRecord RootRecord,
        ulong RootPositionAuthorityVersion,
        IReadOnlyList<ProjectionRemovalCapture> Captures,
        int NextIndex,
        bool RestoreRootRelation,
        bool RestoreDescendantRelations);

    private readonly record struct ProjectionRemovalCapture(
        LiveEntityRecord Record,
        ulong PositionAuthorityVersion,
        ulong ProjectionMutationVersion,
        AttachedChild? Attached,
        bool IsRoot);

    private readonly record struct AttachedRemovalCapture(
        AttachedChild Attached,
        ulong PositionAuthorityVersion,
        ulong ProjectionMutationVersion);

    private readonly record struct PendingOrdinaryRemoval(
        LiveEntityRecord RootRecord,
        IReadOnlyList<AttachedRemovalCapture> Captures,
        int NextIndex);

    private static RuntimeEntityKey RequireProjectionKey(
        LiveEntityRecord record) =>
        record.ProjectionKey
        ?? throw new InvalidOperationException(
            $"Live entity 0x{record.ServerGuid:X8}/{record.Generation} " +
            "has no exact projection key.");
}

public enum ChildUnparentDisposition
{
    NotAttached,
    Completed,
    Pending,
    Superseded,
}

internal enum ParentProjectionValidationDisposition
{
    Ready,
    Waiting,
    Rejected,
}
