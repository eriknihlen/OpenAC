using AcDream.App.World;
using AcDream.Core.Lighting;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Rendering.Vfx;

public sealed class LiveEntityLightController : IDisposable
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly EntityEffectPoseRegistry _poses;
    private readonly LightingHookSink _lighting;
    private readonly Func<uint, Setup?> _loadSetup;
    private readonly HashSet<RuntimeEntityKey> _trackedOwners = [];
    private readonly HashSet<RuntimeEntityKey> _presentOwners = [];

    public LiveEntityLightController(
        LiveEntityRuntime liveEntities,
        EntityEffectPoseRegistry poses,
        LightingHookSink lighting,
        Func<uint, Setup?> loadSetup)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));
        _lighting = lighting ?? throw new ArgumentNullException(nameof(lighting));
        _loadSetup = loadSetup ?? throw new ArgumentNullException(nameof(loadSetup));
        _lighting.OwnerLightingChanged += OnOwnerLightingChanged;
        _liveEntities.ProjectionVisibilityChanged += OnProjectionVisibilityChanged;
    }

    public bool Register(uint serverGuid)
    {
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is not { } entity
            || !record.IsSpatiallyProjected
            || !record.IsSpatiallyVisible)
        {
            return false;
        }

        RuntimeEntityKey key = RequireProjectionKey(record);
        _trackedOwners.Add(key);
        _presentOwners.Add(key);
        _lighting.UnregisterOwner(entity.Id, forgetState: false);
        _lighting.InitializeOwnerLighting(
            entity.Id,
            (record.FinalPhysicsState & PhysicsStateFlags.Lighting) != 0);
        if ((entity.SourceGfxObjOrSetupId & 0xFF000000u) != 0x02000000u)
            return false;

        if (record.ProjectionKind is LiveEntityProjectionKind.World)
        {
            if (!_poses.UpdateRoot(entity))
                _poses.PublishMeshRefs(entity);
        }

        Setup? setup = _loadSetup(entity.SourceGfxObjOrSetupId);
        if (setup is null
            || setup.Lights.Count == 0
            || !_lighting.IsOwnerLightingEnabled(entity.Id))
            return false;

        bool isDynamic = (record.FinalPhysicsState & PhysicsStateFlags.Static) == 0;
        IReadOnlyList<LightSource> loaded = LightInfoLoader.Load(
            setup,
            entity.Id,
            entity.Position,
            entity.Rotation,
            isDynamic,
            entity.ParentCellId ?? 0u,
            tracksOwnerPose: true);
        for (int i = 0; i < loaded.Count; i++)
            _lighting.RegisterOwnedLight(loaded[i]);
        return loaded.Count > 0;
    }

    public void Unregister(uint ownerLocalId)
    {
        if (_liveEntities.TryGetRecordByLocalEntityId(
                ownerLocalId,
                out LiveEntityRecord record)
            && record.ProjectionKey is { } key)
        {
            _presentOwners.Remove(key);
        }
        _lighting.UnregisterOwner(ownerLocalId, forgetState: false);
    }

    /// <summary>Apply a fresh authoritative PhysicsState Lighting transition.</summary>
    public void OnStateChanged(uint serverGuid)
    {
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.WorldEntity is not { } entity)
        {
            return;
        }
        _lighting.SetOwnerLighting(
            entity.Id,
            (record.FinalPhysicsState & PhysicsStateFlags.Lighting) != 0);
    }

    /// <summary>End logical ownership after leave-world presentation teardown.</summary>
    public void Forget(LiveEntityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ProjectionKey is not { } key)
            return;

        _presentOwners.Remove(key);
        _trackedOwners.Remove(key);
        _lighting.UnregisterOwner(key.LocalEntityId);
    }

    public void Refresh() => _lighting.RefreshAttachedLights();

    internal int TrackedOwnerCount => _trackedOwners.Count;
    internal int PresentedOwnerCount => _presentOwners.Count;

    public void OnAttachedPoseReady(uint serverGuid)
    {
        if (!_liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
            || record.ProjectionKind is not LiveEntityProjectionKind.Attached
            || record.WorldEntity is not { } entity
            || record.ProjectionKey is not { } key
            || _presentOwners.Contains(key))
        {
            return;
        }
        Register(serverGuid);
    }

    private void OnOwnerLightingChanged(uint ownerLocalId, bool enabled)
    {
        if (!_liveEntities.TryGetRecordByLocalEntityId(
                ownerLocalId,
                out LiveEntityRecord record)
            || record.ProjectionKey is not { } key
            || !_presentOwners.Contains(key))
        {
            return;
        }

        if (!enabled)
        {
            _lighting.UnregisterOwner(ownerLocalId, forgetState: false);
            return;
        }

        Register(record.ServerGuid);
    }

    public void Dispose()
    {
        _lighting.OwnerLightingChanged -= OnOwnerLightingChanged;
        _liveEntities.ProjectionVisibilityChanged -= OnProjectionVisibilityChanged;
        foreach (RuntimeEntityKey key in _trackedOwners)
            _lighting.UnregisterOwner(key.LocalEntityId);
        _trackedOwners.Clear();
        _presentOwners.Clear();
    }

    private void OnProjectionVisibilityChanged(LiveEntityRecord record, bool visible)
    {
        if (record.WorldEntity is not { } entity)
            return;
        if (record.ProjectionKind is LiveEntityProjectionKind.Attached)
        {
            if (!visible)
                Unregister(entity.Id);
            return;
        }
        if (visible)
            Register(record.ServerGuid);
        else
            Unregister(entity.Id);
    }

    private static RuntimeEntityKey RequireProjectionKey(
        LiveEntityRecord record) =>
        record.ProjectionKey
        ?? throw new InvalidOperationException(
            $"Live entity 0x{record.ServerGuid:X8}/{record.Generation} " +
            "has no exact projection key.");
}
