using System.Numerics;
using AcDream.App.World;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Vfx;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using DatReaderWriter.Types;

namespace AcDream.App.Rendering.Vfx;

public sealed class EntityEffectController : IAnimationHookSink,
    IEntityEffectAdvanceSource
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly PhysicsScriptRunner _runner;
    private readonly PhysicsScriptTableResolver _tables;
    private readonly EntityEffectPoseRegistry _poses;
    private readonly Func<uint, uint, uint?> _childAtPart;
    private readonly Func<uint, uint?> _parentOfAttachedChild;
    private readonly Action<uint> _ownerUnregistered;
    private readonly Action<uint, uint?> _ownerSoundTableChanged;
    private readonly Action<uint, Vector3, uint, float> _playServerSound;
    private readonly Dictionary<RuntimeEntityKey, EntityEffectProfile> _liveProfiles = [];
    private readonly HashSet<RuntimeEntityKey> _readyLiveOwners = [];
    private readonly HashSet<RuntimeEntityKey> _initialPresentationBarriers = [];
    private readonly Dictionary<uint, Queue<PendingEffect>> _pendingByServerGuid = new();
    private readonly Dictionary<uint, WorldEntity> _staticOwners = new();
    private readonly Dictionary<uint, EntityEffectProfile> _staticProfiles = new();
    private readonly HashSet<uint> _syntheticOwners = new();
    private readonly HashSet<LiveEntityRecord> _dirtyLiveOwners =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<LiveEntityRecord> _dirtyLiveOwnerOrder = [];
    private readonly List<LiveEntityRecord> _dirtyLiveOwnerSnapshot = [];
    private readonly List<RuntimeEntityKey> _readyKeySnapshot = [];
    private uint _posePublishLocalId;
    private Action<string>? _diagnosticSink = Console.WriteLine;

    public EntityEffectController(
        LiveEntityRuntime liveEntities,
        PhysicsScriptRunner runner,
        PhysicsScriptTableResolver tables,
        EntityEffectPoseRegistry poses,
        Func<uint, uint, uint?>? childAtPart = null,
        Func<uint, uint?>? parentOfAttachedChild = null,
        Action<uint>? ownerUnregistered = null,
        Action<uint, uint?>? ownerSoundTableChanged = null,
        Action<uint, Vector3, uint, float>? playServerSound = null)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _childAtPart = childAtPart ?? ((_, _) => null);
        _parentOfAttachedChild = parentOfAttachedChild ?? (_ => null);
        _ownerUnregistered = ownerUnregistered ?? (_ => { });
        _ownerSoundTableChanged = ownerSoundTableChanged ?? ((_, _) => { });
        _playServerSound = playServerSound ?? ((_, _, _, _) => { });
        _runner.DiagnosticSink = message => _diagnosticSink?.Invoke(message);
        _poses.EffectPoseChanged += OnEffectPoseChanged;
        _liveEntities.ProjectionVisibilityChanged += OnProjectionVisibilityChanged;
    }

    public Action<string>? DiagnosticSink
    {
        get => _diagnosticSink;
        set => _diagnosticSink = value;
    }

    public int PendingPacketCount => _pendingByServerGuid.Values.Sum(queue => queue.Count);
    public int ReadyOwnerCount => _liveProfiles.Count + _staticProfiles.Count;
    internal int LastPoseRefreshOwnerVisitCount { get; private set; }

    public void HandleDirect(PlayPhysicsScript message)
    {
        if (message.Guid == 0)
            return;
        if (TryGetReadyLocalId(message.Guid, out uint localId))
        {
            RefreshLiveAnchor(message.Guid, localId);
            if (CanStartOwner(localId))
                PlayDirect(localId, message.ScriptDid);
            else if (IsWaitingForInitialPresentation(message.Guid))
                Enqueue(message.Guid, PendingEffect.Direct(message.ScriptDid));
            return;
        }
        Enqueue(message.Guid, PendingEffect.Direct(message.ScriptDid));
    }

    public void HandleSound(SoundEvent message)
    {
        if (message.Guid == 0)
            return;
        if (AcDream.Core.Audio.AudioDiagnostics.ProbeWireSoundsEnabled)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"[sound-wire] recv guid=0x{message.Guid:X8} slot=0x{message.SoundType:X2} vol={message.Volume:F2}"));
        }
        if (TryGetReadyLocalId(message.Guid, out uint localId))
        {
            RefreshLiveAnchor(message.Guid, localId);
            if (CanStartOwner(localId))
                PlayServerSound(localId, message.SoundType, message.Volume);
            else if (IsWaitingForInitialPresentation(message.Guid))
                Enqueue(message.Guid, PendingEffect.Sound(message.SoundType, message.Volume));
            return;
        }
        Enqueue(message.Guid, PendingEffect.Sound(message.SoundType, message.Volume));
    }

    public void HandleTyped(PlayPhysicsScriptType message)
    {
        if (message.Guid == 0)
            return;
        if (TryGetReadyLocalId(message.Guid, out uint localId))
        {
            RefreshLiveAnchor(message.Guid, localId);
            if (CanStartOwner(localId))
                PlayTyped(localId, message.RawScriptType, message.Intensity);
            else if (IsWaitingForInitialPresentation(message.Guid))
            {
                Enqueue(
                    message.Guid,
                    PendingEffect.Typed(message.RawScriptType, message.Intensity));
            }
            return;
        }
        Enqueue(message.Guid, PendingEffect.Typed(message.RawScriptType, message.Intensity));
    }

    /// <summary>
    /// Marks a live owner ready only after its projection, resource owners, and
    /// effect profile have all registered. Pending packets replay once in their
    /// original mixed F754/F755 order.
    /// </summary>
    public bool OnLiveEntityReady(uint serverGuid)
    {
        if (!PrepareLiveEntityOwner(serverGuid))
            return false;
        ReplayPendingForLiveEntity(serverGuid);
        return true;
    }

    public bool PrepareLiveEntityOwner(uint serverGuid)
    {
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is not { } entity
            || !record.ResourcesRegistered
            || record.EffectProfile is not EntityEffectProfile profile)
        {
            return false;
        }

        RuntimeEntityKey key = RequireProjectionKey(record);
        _readyLiveOwners.Add(key);
        _liveProfiles[key] = profile;
        if (record.MaterializationResidence is
                LiveEntityMaterializationResidence.AwaitRuntimePlacement
            && !record.IsSpatiallyProjected)
        {
            _initialPresentationBarriers.Add(key);
        }
        else
        {
            _initialPresentationBarriers.Remove(key);
        }
        _runner.SetOwnerAnchor(entity.Id, entity.Position);
        _ownerSoundTableChanged(entity.Id, profile.CurrentSoundTableDid);
        return true;
    }

    /// <summary>
    /// Opens the C3c-only network-script barrier after the initial placement
    /// has published the entity pose and presentation resources, then replays
    /// the retained mixed F754/F755 FIFO synchronously in arrival order.
    /// </summary>
    public bool OnPresentationBound(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ProjectionKey is not { } key
            || !_liveEntities.TryGetRecord(key, out LiveEntityRecord current)
            || !ReferenceEquals(current, record))
        {
            return false;
        }

        _initialPresentationBarriers.Remove(key);
        if (!TryGetReadyLocalId(record.ServerGuid, out uint localId))
            return true;

        RefreshLiveAnchor(record.ServerGuid, localId);
        TryReplayPending(record.ServerGuid, localId);
        return true;
    }

    public bool ReplayPendingForLiveEntity(uint serverGuid)
    {
        if (!TryGetReadyLocalId(serverGuid, out uint localId))
            return false;
        TryReplayPending(serverGuid, localId);
        return true;
    }

    public bool OnLiveEntityDescriptionChanged(uint serverGuid)
    {
        if (!TryGetReadyLocalId(serverGuid, out uint localId)
            || !_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.EffectProfile is not EntityEffectProfile profile)
        {
            return false;
        }

        _liveProfiles[RequireProjectionKey(record)] = profile;
        _ownerSoundTableChanged(localId, profile.CurrentSoundTableDid);
        return true;
    }

    public void OnDatStaticEntityReady(
        uint ownerLocalId,
        WorldEntity entity,
        EntityEffectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(profile);
        if (ownerLocalId == 0)
            return;
        _staticOwners[ownerLocalId] = entity;
        _staticProfiles[ownerLocalId] = profile;
        _runner.SetOwnerAnchor(ownerLocalId, entity.Position);
        _ownerSoundTableChanged(ownerLocalId, profile.CurrentSoundTableDid);
    }

    public void OnDatStaticEntityRemoved(uint localId)
    {
        if (!_staticOwners.Remove(localId))
            return;
        _staticProfiles.Remove(localId);
        _runner.StopAllForEntity(localId);
        _ownerUnregistered(localId);
    }

    public void RegisterSyntheticOwner(uint ownerLocalId)
    {
        if (ownerLocalId != 0)
            _syntheticOwners.Add(ownerLocalId);
    }

    public void UnregisterSyntheticOwner(uint ownerLocalId) =>
        _syntheticOwners.Remove(ownerLocalId);

    public void OnLiveEntityUnregistered(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_liveEntities.TryGetRecord(record.ServerGuid, out LiveEntityRecord current)
            || ReferenceEquals(current, record))
        {
            _pendingByServerGuid.Remove(record.ServerGuid);
        }
        if (record.ProjectionKey is { } key)
        {
            _readyLiveOwners.Remove(key);
            _liveProfiles.Remove(key);
            _initialPresentationBarriers.Remove(key);
        }
        if (record.LocalEntityId is not { } localId)
            return;
        _runner.StopAllForEntity(localId);
        _ownerUnregistered(localId);
    }

    public void ForgetUnknownOwner(uint serverGuid) =>
        _pendingByServerGuid.Remove(serverGuid);

    public void ClearNetworkState()
    {
        _readyKeySnapshot.Clear();
        _readyKeySnapshot.AddRange(_readyLiveOwners);
        foreach (RuntimeEntityKey key in _readyKeySnapshot)
        {
            _runner.StopAllForEntity(key.LocalEntityId);
            _ownerUnregistered(key.LocalEntityId);
        }
        _readyLiveOwners.Clear();
        _liveProfiles.Clear();
        _initialPresentationBarriers.Clear();
        _pendingByServerGuid.Clear();
        _dirtyLiveOwners.Clear();
        _dirtyLiveOwnerOrder.Clear();
        _dirtyLiveOwnerSnapshot.Clear();
    }

    public void RefreshLiveOwnerPoses()
    {
        LastPoseRefreshOwnerVisitCount = 0;
        if (_dirtyLiveOwnerOrder.Count == 0)
            return;

        _dirtyLiveOwnerSnapshot.Clear();
        _dirtyLiveOwnerSnapshot.AddRange(_dirtyLiveOwnerOrder);
        _dirtyLiveOwnerOrder.Clear();
        _dirtyLiveOwners.Clear();
        foreach (LiveEntityRecord record in _dirtyLiveOwnerSnapshot)
        {
            if (!_liveEntities.TryGetRecord(record.ServerGuid, out LiveEntityRecord current)
                || !ReferenceEquals(current, record)
                || !TryGetReadyLocalId(record.ServerGuid, out uint localId)
                || record.WorldEntity?.Id != localId)
            {
                continue;
            }

            LastPoseRefreshOwnerVisitCount++;
            RefreshLiveAnchor(record.ServerGuid, localId);
            TryReplayPending(record.ServerGuid, localId);
        }
    }

    /// <summary>
    /// Marks an accepted authoritative root mutation. The record reference,
    /// rather than only its server GUID, isolates a queued update from later
    /// delete/recreate reuse of that GUID.
    /// </summary>
    public void MarkLiveOwnerPoseDirty(uint serverGuid)
    {
        if (_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record))
            MarkLiveOwnerPoseDirty(record);
    }

    public bool PlayDirect(uint ownerLocalId, uint scriptDid)
    {
        if (!CanStartOwner(ownerLocalId))
            return false;
        return _runner.PlayDirect(ownerLocalId, scriptDid);
    }

    private void PlayServerSound(uint ownerLocalId, uint soundType, float volume)
    {
        if (!CanStartOwner(ownerLocalId))
            return;

        Vector3 anchor = _poses.TryGetRootPose(ownerLocalId, out Matrix4x4 rootWorld)
            ? rootWorld.Translation
            : Vector3.Zero;
        _playServerSound(ownerLocalId, anchor, soundType, volume);
    }

    public bool PlayTyped(uint ownerLocalId, uint rawScriptType, float intensity)
    {
        if (!CanStartOwner(ownerLocalId))
            return false;
        return ResolveAndQueueTyped(ownerLocalId, rawScriptType, intensity);
    }

    public bool PlayTypedFromHiddenTransition(
        uint ownerLocalId,
        uint rawScriptType,
        float intensity)
        => ResolveAndQueueTyped(ownerLocalId, rawScriptType, intensity);

    private bool ResolveAndQueueTyped(
        uint ownerLocalId,
        uint rawScriptType,
        float intensity)
    {
        if (!TryGetProfile(ownerLocalId, out EntityEffectProfile? profile)
            || profile.CurrentPhysicsScriptTableDid is not { } tableDid)
        {
            DiagnosticSink?.Invoke(
                $"No PhysicsScriptTable for owner 0x{ownerLocalId:X8}, type 0x{rawScriptType:X8}.");
            return false;
        }

        uint? scriptDid = _tables.Resolve(
            tableDid,
            rawScriptType,
            intensity,
            out Exception? loadFailure);
        if (scriptDid is not { } resolved)
        {
            string detail = loadFailure is null
                ? string.Empty
                : $" Load failed: {loadFailure.GetType().Name}: {loadFailure.Message}";
            DiagnosticSink?.Invoke(
                $"No typed PhysicsScript for owner 0x{ownerLocalId:X8}, table 0x{tableDid:X8}, " +
                $"type 0x{rawScriptType:X8}, intensity {intensity:R}.{detail}");
            return false;
        }
        return _runner.PlayDirect(ownerLocalId, resolved);
    }

    public bool PlayDefault(uint ownerLocalId)
    {
        if (!CanStartOwner(ownerLocalId)
            || !TryGetProfile(ownerLocalId, out EntityEffectProfile? profile))
            return false;
        return PlayTyped(
            ownerLocalId,
            profile.RawDefaultScriptType,
            profile.DefaultScriptIntensity);
    }

    public void OnHook(uint entityId, Vector3 entityWorldPosition, AnimationHook hook)
    {
        _runner.SetOwnerAnchor(entityId, entityWorldPosition);
        switch (hook)
        {
            case CallPESHook call:
                if (CanStartOwner(entityId))
                    _runner.ScheduleCallPes(entityId, call.PES, call.Pause);
                break;
            case DefaultScriptHook:
                PlayDefault(entityId);
                break;
            case DefaultScriptPartHook part:
                if (_childAtPart(entityId, part.PartIndex) is { } childLocalId)
                    PlayDefault(childLocalId);
                break;
        }
    }

    public bool CanAdvanceOwner(uint ownerLocalId)
    {
        if (_staticOwners.ContainsKey(ownerLocalId) || _syntheticOwners.Contains(ownerLocalId))
            return true;

        if (_parentOfAttachedChild(ownerLocalId) is { } parentLocalId)
            return CanAdvanceLiveRoot(parentLocalId);

        return CanAdvanceLiveRoot(ownerLocalId);
    }

    private bool CanStartOwner(uint ownerLocalId)
    {
        if (_staticOwners.ContainsKey(ownerLocalId) || _syntheticOwners.Contains(ownerLocalId))
            return true;
        if (_parentOfAttachedChild(ownerLocalId) is { } parentLocalId)
            return IsLiveRootInCell(parentLocalId);
        return IsLiveRootInCell(ownerLocalId);
    }

    private bool CanAdvanceLiveRoot(uint ownerLocalId)
    {
        if (!TryGetLiveRoot(ownerLocalId, out LiveEntityRecord record))
            return false;
        return IsLiveRootInCell(record)
            && (record.FinalPhysicsState & PhysicsStateFlags.Frozen) == 0;
    }

    private bool IsLiveRootInCell(uint ownerLocalId) =>
        TryGetLiveRoot(ownerLocalId, out LiveEntityRecord record)
        && IsLiveRootInCell(record);

    private static bool IsLiveRootInCell(LiveEntityRecord record) =>
        record.ResourcesRegistered
        && record.IsSpatiallyProjected
        && record.IsSpatiallyVisible
        && record.FullCellId != 0;

    private bool TryGetLiveRoot(uint ownerLocalId, out LiveEntityRecord record)
    {
        if (_liveEntities.TryGetRecordByLocalEntityId(
                ownerLocalId,
                out record!))
        {
            return true;
        }
        record = null!;
        return false;
    }

    private bool TryGetReadyLocalId(uint serverGuid, out uint localId)
    {
        if (_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            && record.ProjectionKey is { } key
            && _readyLiveOwners.Contains(key)
            && _liveProfiles.ContainsKey(key))
        {
            localId = key.LocalEntityId;
            return true;
        }

        localId = 0;
        return false;
    }

    private bool IsWaitingForInitialPresentation(uint serverGuid) =>
        _liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
        && record.ProjectionKey is { } key
        && _initialPresentationBarriers.Contains(key);

    private void OnEffectPoseChanged(uint localId)
    {
        if (_posePublishLocalId == localId)
            return;
        if (_liveEntities.TryGetRecordByLocalEntityId(
                localId,
                out LiveEntityRecord record))
        {
            MarkLiveOwnerPoseDirty(record);
        }
    }

    private void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
    {
        if (visible)
            MarkLiveOwnerPoseDirty(record);
    }

    private void MarkLiveOwnerPoseDirty(LiveEntityRecord record)
    {
        if (record.ProjectionKey is not { } key
            || !_readyLiveOwners.Contains(key)
            || !_dirtyLiveOwners.Add(record))
        {
            return;
        }

        _dirtyLiveOwnerOrder.Add(record);
    }

    private void RefreshLiveAnchor(uint serverGuid, uint localId)
    {
        if (_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            && record.WorldEntity is { } entity
            && entity.Id == localId)
        {
            if (record.ProjectionKind is LiveEntityProjectionKind.World)
            {
                uint previousPublish = _posePublishLocalId;
                _posePublishLocalId = localId;
                try
                {
                    _poses.UpdateRoot(entity);
                }
                finally
                {
                    _posePublishLocalId = previousPublish;
                }
            }

            Vector3 anchor = _poses.TryGetRootPose(localId, out Matrix4x4 rootWorld)
                ? rootWorld.Translation
                : entity.Position;
            _runner.SetOwnerAnchor(localId, anchor);
        }
    }

    private void Enqueue(uint serverGuid, PendingEffect effect)
    {
        if (!_pendingByServerGuid.TryGetValue(serverGuid, out Queue<PendingEffect>? queue))
        {
            queue = new Queue<PendingEffect>();
            _pendingByServerGuid.Add(serverGuid, queue);
        }
        queue.Enqueue(effect);
    }

    private void TryReplayPending(uint serverGuid, uint localId)
    {
        if (!CanStartOwner(localId)
            || !_pendingByServerGuid.Remove(serverGuid, out Queue<PendingEffect>? pending))
        {
            return;
        }

        while (pending.Count > 0)
            Execute(localId, pending.Dequeue());
    }

    private void Execute(uint localId, PendingEffect effect)
    {
        switch (effect.Kind)
        {
            case PendingEffectKind.Direct:
                PlayDirect(localId, effect.ScriptDid);
                break;
            case PendingEffectKind.Typed:
                PlayTyped(localId, effect.RawScriptType, effect.Intensity);
                break;
            case PendingEffectKind.Sound:
                PlayServerSound(localId, effect.RawScriptType, effect.Intensity);
                break;
        }
    }

    private bool TryGetProfile(
        uint ownerLocalId,
        out EntityEffectProfile profile)
    {
        if (_liveEntities.TryGetRecordByLocalEntityId(
                ownerLocalId,
                out LiveEntityRecord record)
            && record.ProjectionKey is { } key
            && _liveProfiles.TryGetValue(key, out profile!))
        {
            return true;
        }

        return _staticProfiles.TryGetValue(ownerLocalId, out profile!);
    }

    private static RuntimeEntityKey RequireProjectionKey(
        LiveEntityRecord record) =>
        record.ProjectionKey
        ?? throw new InvalidOperationException(
            $"Live entity 0x{record.ServerGuid:X8}/{record.Generation} " +
            "has no exact projection key.");

    private enum PendingEffectKind
    {
        Direct,
        Typed,
        Sound,
    }

    private readonly record struct PendingEffect(
        PendingEffectKind Kind,
        uint ScriptDid,
        uint RawScriptType,
        float Intensity)
    {
        public static PendingEffect Direct(uint scriptDid) =>
            new(PendingEffectKind.Direct, scriptDid, 0u, 0f);

        public static PendingEffect Typed(uint rawScriptType, float intensity) =>
            new(PendingEffectKind.Typed, 0u, rawScriptType, intensity);

        public static PendingEffect Sound(uint soundType, float volume) =>
            new(PendingEffectKind.Sound, 0u, soundType, volume);
    }
}
