using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Rendering.Wb;
using AcDream.App.World;
using AcDream.Core.World;
using AcDream.Runtime.Entities;

namespace AcDream.App.Streaming;

internal sealed record GpuLandblockSpatialPublication(
    uint LandblockId,
    LoadedLandblock Landblock,
    IReadOnlyList<ulong> AdditionalRenderIds,
    IReadOnlyList<WorldEntity> StaticEntities,
    bool RequiresActivation = true,
    uint RenderTraversalOrder = 0,
    IReadOnlyList<ulong>? AdditionalOrdinaryRenderIds = null,
    bool ReplacesMeshRegistration = false);

public sealed class GpuWorldState : ILiveEntitySpatialQuery
{
    private readonly LandblockSpawnAdapter? _wbSpawnAdapter;
    private readonly EntityScriptActivator? _entityScriptActivator;
    private readonly IWorldGenerationAvailability _availability;

    private readonly System.Action<uint>? _onLandblockUnloaded;

    public GpuWorldState(
        LandblockSpawnAdapter? wbSpawnAdapter = null,
        System.Action<uint>? onLandblockUnloaded = null,
        EntityScriptActivator? entityScriptActivator = null,
        IWorldGenerationAvailability? availability = null)
    {
        _wbSpawnAdapter = wbSpawnAdapter;
        _onLandblockUnloaded = onLandblockUnloaded;
        _entityScriptActivator = entityScriptActivator;
        _availability = availability ?? AlwaysAvailableWorldGeneration.Instance;
    }

    private readonly Dictionary<uint, LoadedLandblock> _loaded = new();
    private readonly List<uint> _renderTraversalLandblockSlots = [];
    private readonly Stack<int> _freeRenderTraversalLandblockSlots = [];
    private readonly Dictionary<uint, int> _renderTraversalSlotByLandblock = [];
    private readonly Dictionary<uint, LandblockStreamTier> _tierByLandblock = new();
    private readonly Dictionary<uint, (Vector3 Min, Vector3 Max)> _aabbs = new();
    private readonly Dictionary<uint, AnimatedEntityIndex> _animatedIndexByLandblock = new();
    private readonly List<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)>
        _landblockEntriesView = [];
    private readonly List<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
        IReadOnlyList<WorldEntity> Entities,
        IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)>
        _landblockEntriesWithoutAnimatedIndexView = [];
    private readonly List<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)>
        _landblockBoundsView = [];
    private bool _landblockEntriesViewDirty = true;
    private bool _landblockEntriesWithoutAnimatedIndexViewDirty = true;
    private bool _landblockBoundsViewDirty = true;

    private sealed record AnimatedEntityIndex(
        LoadedLandblock Source,
        IReadOnlyDictionary<uint, WorldEntity> EntitiesById);

    private readonly Dictionary<uint, List<WorldEntity>> _pendingByLandblock = new();
    private readonly Dictionary<uint, HashSet<ulong>> _pendingRenderIdsByLandblock = new();
    private readonly HashSet<uint> _pendingNearTierLandblocks = new();

    private readonly HashSet<uint> _persistentGuids = new();

    private readonly List<WorldEntity> _flatEntities = new();
    private readonly Dictionary<WorldEntity, int> _flatEntityIndices =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WorldEntity, ProjectionLocation> _projectionLocations =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<uint, HashSet<WorldEntity>> _loadedLiveByLandblock = new();
    private readonly Dictionary<RuntimeEntityKey, WorldEntity> _liveProjectionByKey = new();
    private readonly Dictionary<RuntimeEntityKey, int> _visibleLiveProjectionCounts = new();
    private readonly Dictionary<RuntimeEntityKey, bool> _visibilityBeforeMutation = new();
    private readonly Queue<(RuntimeEntityKey Key, bool Visible)> _visibilityTransitions = new();
    private readonly List<WorldEntity> _guidRemovalScratch = new();
    private bool _dispatchingVisibilityTransitions;
    private int _mutationDepth;
    private long _visibilityCommitCount;
    private ulong _flatViewGeneration;
    private ulong _residentWindowRevision;
    private bool _flatMembershipDirty;

    public IReadOnlyList<WorldEntity> Entities => _flatEntities;
    public ulong FlatViewGeneration => _flatViewGeneration;
    public IReadOnlyCollection<uint> LoadedLandblockIds => _loaded.Keys;
    internal ulong ResidentWindowRevision => _residentWindowRevision;
    public event Action<RuntimeEntityKey, bool>? LiveProjectionVisibilityChanged;

    public bool IsLoaded(uint landblockId) => _loaded.ContainsKey(landblockId);
    public bool IsNearTier(uint landblockId) =>
        _tierByLandblock.TryGetValue(landblockId, out var tier)
        && tier == LandblockStreamTier.Near;
    public bool IsNearTierOrPending(uint landblockId) =>
        IsNearTier(landblockId) || _pendingNearTierLandblocks.Contains(landblockId);
    public bool IsRenderReady(uint landblockId) =>
        _loaded.ContainsKey(landblockId)
        && (_wbSpawnAdapter?.IsLandblockRenderReady(landblockId) ?? true);
    public bool IsLiveEntityProjectionResident(RuntimeEntityKey key) =>
        key.LocalEntityId != 0
        && _visibleLiveProjectionCounts.ContainsKey(key);

    public bool IsLiveEntityVisible(RuntimeEntityKey key) =>
        _availability.IsWorldAvailable
        && IsLiveEntityProjectionResident(key);

    public int LoadedLandblockCount => _loaded.Count;

    public bool TryGetLandblock(uint landblockId, out LoadedLandblock? lb)
    {
        if (_loaded.TryGetValue(landblockId, out var found))
        {
            lb = found;
            return true;
        }
        lb = null;
        return false;
    }

    public void SetLandblockAabb(uint landblockId, Vector3 min, Vector3 max)
    {
        _aabbs[landblockId] = (min, max);
        InvalidateLandblockRenderViews(entries: true, bounds: true);
    }

    public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                         IReadOnlyList<WorldEntity> Entities,
                         IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> LandblockEntries
        => GetLandblockEntriesView(includeAnimatedIndex: true);

    public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                         IReadOnlyList<WorldEntity> Entities,
                         IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> LandblockEntriesWithoutAnimatedIndex
        => GetLandblockEntriesView(includeAnimatedIndex: false);

    /// <summary>
    /// Lightweight bounds-only enumeration for overlays and diagnostics.
    /// Does not walk entity lists.
    /// </summary>
    public IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax)> LandblockBounds
    {
        get
        {
            if (!_availability.IsWorldAvailable)
                return Array.Empty<(uint, Vector3, Vector3)>();
            if (!_landblockBoundsViewDirty)
                return _landblockBoundsView;

            _landblockBoundsView.Clear();
            for (int slot = 0; slot < _renderTraversalLandblockSlots.Count; slot++)
            {
                uint landblockId = _renderTraversalLandblockSlots[slot];
                if (landblockId == 0)
                    continue;
                if (_aabbs.TryGetValue(landblockId, out var aabb))
                    _landblockBoundsView.Add((landblockId, aabb.Min, aabb.Max));
                else
                    _landblockBoundsView.Add(
                        (landblockId, Vector3.Zero, Vector3.Zero));
            }

            _landblockBoundsViewDirty = false;
            return _landblockBoundsView;
        }
    }

    private IReadOnlyList<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                           IReadOnlyList<WorldEntity> Entities,
                           IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> GetLandblockEntriesView(
        bool includeAnimatedIndex)
    {
        if (!_availability.IsWorldAvailable)
        {
            return Array.Empty<(uint, Vector3, Vector3,
                IReadOnlyList<WorldEntity>,
                IReadOnlyDictionary<uint, WorldEntity>?)>();
        }

        List<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
            IReadOnlyList<WorldEntity> Entities,
            IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> view =
            includeAnimatedIndex
                ? _landblockEntriesView
                : _landblockEntriesWithoutAnimatedIndexView;
        bool dirty = includeAnimatedIndex
            ? _landblockEntriesViewDirty
            : _landblockEntriesWithoutAnimatedIndexViewDirty;
        if (!dirty)
            return view;

        view.Clear();

        for (int slot = 0; slot < _renderTraversalLandblockSlots.Count; slot++)
        {
            uint landblockId = _renderTraversalLandblockSlots[slot];
            if (landblockId == 0)
                continue;
            LoadedLandblock landblock = _loaded[landblockId];
            IReadOnlyDictionary<uint, WorldEntity>? byId = null;
            if (includeAnimatedIndex)
            {
                if (!_animatedIndexByLandblock.TryGetValue(landblockId, out var cached)
                    || !ReferenceEquals(cached.Source, landblock))
                {
                    var rebuilt = new Dictionary<uint, WorldEntity>(landblock.Entities.Count);
                    foreach (var entity in landblock.Entities)
                        rebuilt[entity.Id] = entity;

                    cached = new AnimatedEntityIndex(landblock, rebuilt);
                    _animatedIndexByLandblock[landblockId] = cached;
                }

                byId = cached.EntitiesById;
            }

            if (_aabbs.TryGetValue(landblockId, out var aabb))
                view.Add(
                    (landblockId, aabb.Min, aabb.Max, landblock.Entities, byId));
            else
                view.Add(
                    (landblockId, Vector3.Zero, Vector3.Zero, landblock.Entities, byId));
        }

        if (includeAnimatedIndex)
            _landblockEntriesViewDirty = false;
        else
            _landblockEntriesWithoutAnimatedIndexViewDirty = false;
        return view;
    }

    private void InvalidateLandblockRenderViews(bool entries, bool bounds)
    {
        if (entries)
        {
            _landblockEntriesViewDirty = true;
            _landblockEntriesWithoutAnimatedIndexViewDirty = true;
        }
        if (bounds)
            _landblockBoundsViewDirty = true;
    }

    internal bool TryGetRenderTraversalSortKey(
        WorldEntity entity,
        out RenderSortKey sortKey)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_projectionLocations.TryGetValue(
                entity,
                out ProjectionLocation location)
            && location.IsLoaded
            && _renderTraversalSlotByLandblock.TryGetValue(
                location.LandblockId,
                out int landblockSlot))
        {
            sortKey = RenderTraversalSortKey.Compose(
                checked((uint)landblockSlot + 1),
                location.BucketIndex);
            return true;
        }

        sortKey = default;
        return false;
    }

    private uint GetRenderTraversalOrder(uint landblockId)
    {
        if (!_renderTraversalSlotByLandblock.TryGetValue(
                landblockId,
                out int slot))
        {
            throw new InvalidOperationException(
                $"Loaded landblock 0x{landblockId:X8} has no render traversal slot.");
        }

        return checked((uint)slot + 1);
    }

    private void AddLoadedLandblock(
        uint landblockId,
        LoadedLandblock landblock)
    {
        if (!_loaded.TryAdd(landblockId, landblock))
        {
            throw new InvalidOperationException(
                $"Landblock 0x{landblockId:X8} is already loaded.");
        }

        int slot;
        if (_freeRenderTraversalLandblockSlots.TryPop(out int freeSlot))
        {
            slot = freeSlot;
            if (_renderTraversalLandblockSlots[slot] != 0)
            {
                throw new InvalidOperationException(
                    $"Render traversal slot {slot} was not free.");
            }
            _renderTraversalLandblockSlots[slot] = landblockId;
        }
        else
        {
            slot = _renderTraversalLandblockSlots.Count;
            _renderTraversalLandblockSlots.Add(landblockId);
        }

        if (!_renderTraversalSlotByLandblock.TryAdd(landblockId, slot))
        {
            throw new InvalidOperationException(
                $"Landblock 0x{landblockId:X8} already owns a render traversal slot.");
        }
        _residentWindowRevision = checked(_residentWindowRevision + 1);
        InvalidateLandblockRenderViews(entries: true, bounds: true);
    }

    private bool RemoveLoadedLandblock(
        uint landblockId,
        [NotNullWhen(true)]
        out LoadedLandblock? landblock)
    {
        if (!_loaded.Remove(landblockId, out landblock))
            return false;
        if (!_renderTraversalSlotByLandblock.Remove(
                landblockId,
                out int slot)
            || _renderTraversalLandblockSlots[slot] != landblockId)
        {
            throw new InvalidOperationException(
                $"Loaded landblock 0x{landblockId:X8} has inconsistent render traversal state.");
        }

        _renderTraversalLandblockSlots[slot] = 0;
        _freeRenderTraversalLandblockSlots.Push(slot);
        _residentWindowRevision = checked(_residentWindowRevision + 1);
        InvalidateLandblockRenderViews(entries: true, bounds: true);
        return true;
    }

    internal ResidentStreamingWindowFact CaptureResidentStreamingWindow(
        int centerX,
        int centerY)
    {
        if ((uint)centerX > byte.MaxValue || (uint)centerY > byte.MaxValue)
            return ResidentStreamingWindowFact.Unavailable(_residentWindowRevision);

        uint center = StreamingRegion.EncodeLandblockId(centerX, centerY);
        if (!_loaded.ContainsKey(center))
            return ResidentStreamingWindowFact.Unavailable(_residentWindowRevision);

        int maximumCandidate = 0;
        foreach (uint landblockId in _loaded.Keys)
        {
            int x = (int)((landblockId >> 24) & 0xFFu);
            int y = (int)((landblockId >> 16) & 0xFFu);
            maximumCandidate = Math.Max(
                maximumCandidate,
                Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY)));
        }

        int completeRadius = 0;
        for (int radius = 1; radius <= maximumCandidate; radius++)
        {
            if (!ContainsCompleteRing(radius))
                break;
            completeRadius = radius;
        }

        return new ResidentStreamingWindowFact(
            _residentWindowRevision,
            centerX,
            centerY,
            completeRadius,
            _loaded.Count,
            HasPublishedCenter: true);

        bool ContainsCompleteRing(int radius)
        {
            int minX = Math.Max(0, centerX - radius);
            int maxX = Math.Min(byte.MaxValue, centerX + radius);
            int minY = Math.Max(0, centerY - radius);
            int maxY = Math.Min(byte.MaxValue, centerY + radius);
            for (int x = minX; x <= maxX; x++)
            {
                if (!_loaded.ContainsKey(StreamingRegion.EncodeLandblockId(x, minY))
                    || !_loaded.ContainsKey(StreamingRegion.EncodeLandblockId(x, maxY)))
                {
                    return false;
                }
            }
            for (int y = minY + 1; y < maxY; y++)
            {
                if (!_loaded.ContainsKey(StreamingRegion.EncodeLandblockId(minX, y))
                    || !_loaded.ContainsKey(StreamingRegion.EncodeLandblockId(maxX, y)))
                {
                    return false;
                }
            }
            return true;
        }
    }

    public int PendingLiveEntityCount => _pendingByLandblock.Values.Sum(list => list.Count);
    public int PendingBucketCount => _pendingByLandblock.Count;
    public int PendingRescueCount => _persistentRescued.Count;
    public int PersistentGuidCount => _persistentGuids.Count;
    public int PendingVisibilityTransitionCount => _visibilityTransitions.Count;
    internal long VisibilityCommitCount => _visibilityCommitCount;
    internal int OriginRecenterSpatialOperationCount =>
        Math.Max(
            1,
            _loaded.Count
            + _pendingByLandblock.Count
            + _pendingRenderIdsByLandblock.Count
            + _pendingNearTierLandblocks.Count
            + _projectionLocations.Count);

    public MutationBatch BeginMutationBatch()
    {
        _mutationDepth++;
        return new MutationBatch(this);
    }

    public void CopyLiveEntitiesNearLandblock(
        uint centerCellOrLandblockId,
        int landblockRadius,
        List<KeyValuePair<uint, WorldEntity>> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(landblockRadius);
        destination.Clear();
        if (!_availability.IsWorldAvailable)
            return;

        int centerX = (int)((centerCellOrLandblockId >> 24) & 0xFFu);
        int centerY = (int)((centerCellOrLandblockId >> 16) & 0xFFu);
        for (int dx = -landblockRadius; dx <= landblockRadius; dx++)
        for (int dy = -landblockRadius; dy <= landblockRadius; dy++)
        {
            int x = centerX + dx;
            int y = centerY + dy;
            if ((uint)x > 0xFFu || (uint)y > 0xFFu)
                continue;

            uint landblockId = ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;
            if (!_loadedLiveByLandblock.TryGetValue(
                    landblockId,
                    out HashSet<WorldEntity>? liveEntities))
                continue;

            foreach (WorldEntity entity in liveEntities)
                destination.Add(new KeyValuePair<uint, WorldEntity>(entity.ServerGuid, entity));
        }
    }

    internal void CopyPublishedEntitiesNearLandblockForReadiness(
        uint centerCellOrLandblockId,
        int landblockRadius,
        List<WorldEntity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(landblockRadius);
        destination.Clear();

        int centerX = (int)((centerCellOrLandblockId >> 24) & 0xFFu);
        int centerY = (int)((centerCellOrLandblockId >> 16) & 0xFFu);
        for (int dx = -landblockRadius; dx <= landblockRadius; dx++)
        for (int dy = -landblockRadius; dy <= landblockRadius; dy++)
        {
            int x = centerX + dx;
            int y = centerY + dy;
            if ((uint)x > 0xFFu || (uint)y > 0xFFu)
                continue;

            uint landblockId =
                ((uint)x << 24) | ((uint)y << 16) | 0xFFFFu;
            if (!_loaded.TryGetValue(
                    landblockId,
                    out LoadedLandblock? landblock))
            {
                continue;
            }

            for (int i = 0; i < landblock.Entities.Count; i++)
                destination.Add(landblock.Entities[i]);
        }
    }

    public readonly ref struct MutationBatch
    {
        private readonly GpuWorldState _owner;

        public MutationBatch(GpuWorldState owner) => _owner = owner;

        public void Dispose() => _owner.EndMutationBatch();
    }

    private readonly record struct ProjectionLocation(
        uint LandblockId,
        bool IsLoaded,
        int BucketIndex,
        RuntimeEntityKey Key);

    private void EndMutationBatch()
    {
        if (_mutationDepth <= 0)
            throw new InvalidOperationException("GpuWorldState mutation scope underflow.");
        if (--_mutationDepth != 0)
            return;

        foreach ((RuntimeEntityKey key, bool wasVisible) in _visibilityBeforeMutation)
        {
            bool isVisible = _visibleLiveProjectionCounts.ContainsKey(key);
            if (wasVisible != isVisible)
                _visibilityTransitions.Enqueue((key, isVisible));
        }
        _visibilityBeforeMutation.Clear();
        _visibilityCommitCount++;
        if (_flatMembershipDirty)
        {
            _flatViewGeneration++;
            _flatMembershipDirty = false;
        }

        DrainVisibilityTransitions();
        if (EntityVanishProbe.Enabled)
            ProbeFlatViewTransitions();
    }

    private void AdjustVisibleLiveProjection(
        RuntimeEntityKey key,
        WorldEntity entity,
        int delta)
    {
        if (entity.ServerGuid == 0 || key.LocalEntityId == 0 || delta == 0)
            return;
        if (key.LocalEntityId != entity.Id)
            throw new InvalidOperationException(
                "A live visibility key must match its WorldEntity local ID.");

        _visibleLiveProjectionCounts.TryGetValue(key, out int priorCount);
        if (!_visibilityBeforeMutation.ContainsKey(key))
            _visibilityBeforeMutation.Add(key, priorCount > 0);

        int nextCount = checked(priorCount + delta);
        if (nextCount < 0)
            throw new InvalidOperationException(
                $"Live projection visibility count underflow for local " +
                $"0x{key.LocalEntityId:X8}/{key.Incarnation} / " +
                $"server 0x{entity.ServerGuid:X8}.");

        if (nextCount == 0)
        {
            _visibleLiveProjectionCounts.Remove(key);
        }
        else
        {
            _visibleLiveProjectionCounts[key] = nextCount;
        }
    }

    private static LoadedLandblock NormalizeLoadedLandblock(
        LoadedLandblock source,
        List<WorldEntity>? entities = null) =>
        new(
            source.LandblockId,
            source.Heightmap,
            entities ?? new List<WorldEntity>(source.Entities),
            PhysicsDatBundle.Empty);

    private static List<WorldEntity> MutableEntities(LoadedLandblock landblock) =>
        (List<WorldEntity>)landblock.Entities;

    private void AddFlatEntity(WorldEntity entity)
    {
        if (!_flatEntityIndices.TryAdd(entity, _flatEntities.Count))
            throw new InvalidOperationException(
                $"Entity 0x{entity.Id:X8} already exists in the flat render view.");
        _flatEntities.Add(entity);
        _flatMembershipDirty = true;
    }

    private void RemoveFlatEntity(WorldEntity entity)
    {
        if (!_flatEntityIndices.Remove(entity, out int index))
            throw new InvalidOperationException(
                $"Loaded entity 0x{entity.Id:X8} was absent from the flat render view.");

        int lastIndex = _flatEntities.Count - 1;
        if (index != lastIndex)
        {
            WorldEntity moved = _flatEntities[lastIndex];
            _flatEntities[index] = moved;
            _flatEntityIndices[moved] = index;
        }
        _flatEntities.RemoveAt(lastIndex);
        _flatMembershipDirty = true;
    }

    private void SetProjectionLocation(
        WorldEntity entity,
        uint landblockId,
        bool isLoaded,
        int bucketIndex,
        RuntimeEntityKey? requestedKey = null)
    {
        if (entity.ServerGuid == 0)
            return;

        bool isNew = !_projectionLocations.TryGetValue(
            entity,
            out ProjectionLocation priorLocation);
        RuntimeEntityKey key = isNew
            ? requestedKey ?? new RuntimeEntityKey(entity.Id, 0)
            : priorLocation.Key;
        if (key.LocalEntityId == 0 || key.LocalEntityId != entity.Id)
        {
            throw new InvalidOperationException(
                "The exact spatial projection key must match the WorldEntity local ID.");
        }
        if (!isNew && requestedKey is { } requested && requested != key)
        {
            throw new InvalidOperationException(
                "A live spatial projection cannot change exact Runtime identity.");
        }
        if (!isNew
            && priorLocation.IsLoaded
            && (!isLoaded || priorLocation.LandblockId != landblockId))
        {
            RemoveLoadedLiveIndex(priorLocation.LandblockId, entity);
        }
        _projectionLocations[entity] = new ProjectionLocation(
            landblockId,
            isLoaded,
            bucketIndex,
            key);
        if (isLoaded
            && (isNew || !priorLocation.IsLoaded || priorLocation.LandblockId != landblockId))
        {
            AddLoadedLiveIndex(landblockId, entity);
        }
        if (!isNew)
            return;

        if (!_liveProjectionByKey.TryAdd(key, entity))
        {
            throw new InvalidOperationException(
                $"Runtime projection {key} is already spatially owned.");
        }
    }

    private void RemoveProjectionLocation(WorldEntity entity)
    {
        if (!_projectionLocations.Remove(entity, out ProjectionLocation location)
            || entity.ServerGuid == 0)
            return;
        if (location.IsLoaded)
            RemoveLoadedLiveIndex(location.LandblockId, entity);

        if (!_liveProjectionByKey.Remove(
                location.Key,
                out WorldEntity? indexed))
        {
            throw new InvalidOperationException(
                $"Live projection {location.Key} had no exact-key index entry.");
        }
        if (!ReferenceEquals(indexed, entity))
        {
            throw new InvalidOperationException(
                $"Runtime projection {location.Key} resolved a different owner.");
        }
    }

    private void AddLoadedLiveIndex(uint landblockId, WorldEntity entity)
    {
        if (!_loadedLiveByLandblock.TryGetValue(
                landblockId,
                out HashSet<WorldEntity>? entities))
        {
            entities = new HashSet<WorldEntity>(ReferenceEqualityComparer.Instance);
            _loadedLiveByLandblock.Add(landblockId, entities);
        }
        if (!entities.Add(entity))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{entity.Id:X8} already exists in landblock index 0x{landblockId:X8}.");
        }
    }

    private void RemoveLoadedLiveIndex(uint landblockId, WorldEntity entity)
    {
        if (!_loadedLiveByLandblock.TryGetValue(
                landblockId,
                out HashSet<WorldEntity>? entities)
            || !entities.Remove(entity))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{entity.Id:X8} is absent from landblock index 0x{landblockId:X8}.");
        }
        if (entities.Count == 0)
            _loadedLiveByLandblock.Remove(landblockId);
    }

    private void AddLoadedProjection(
        uint landblockId,
        WorldEntity entity,
        RuntimeEntityKey? requestedKey = null)
    {
        LoadedLandblock landblock = _loaded[landblockId];
        List<WorldEntity> entities = MutableEntities(landblock);
        int bucketIndex = entities.Count;
        entities.Add(entity);
        _animatedIndexByLandblock.Remove(landblockId);
        InvalidateLandblockRenderViews(entries: true, bounds: false);
        AddFlatEntity(entity);
        if (entity.ServerGuid != 0)
        {
            SetProjectionLocation(
                entity,
                landblockId,
                isLoaded: true,
                bucketIndex,
                requestedKey);
            AdjustVisibleLiveProjection(
                _projectionLocations[entity].Key,
                entity,
                +1);
        }
    }

    private void RemoveLoadedProjection(WorldEntity entity)
    {
        if (!_projectionLocations.TryGetValue(entity, out ProjectionLocation location)
            || !location.IsLoaded
            || !_loaded.TryGetValue(location.LandblockId, out LoadedLandblock? landblock))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{entity.Id:X8} has no loaded projection location.");
        }

        List<WorldEntity> entities = MutableEntities(landblock);
        int lastIndex = entities.Count - 1;
        if ((uint)location.BucketIndex >= (uint)entities.Count
            || !ReferenceEquals(entities[location.BucketIndex], entity))
        {
            throw new InvalidOperationException(
                $"Loaded projection index for entity 0x{entity.Id:X8} is stale.");
        }
        if (location.BucketIndex != lastIndex)
        {
            WorldEntity moved = entities[lastIndex];
            entities[location.BucketIndex] = moved;
            if (moved.ServerGuid != 0)
            {
                SetProjectionLocation(
                    moved,
                    location.LandblockId,
                    isLoaded: true,
                    location.BucketIndex);
            }
        }
        entities.RemoveAt(lastIndex);

        _animatedIndexByLandblock.Remove(location.LandblockId);
        InvalidateLandblockRenderViews(entries: true, bounds: false);
        RemoveFlatEntity(entity);
        RemoveProjectionLocation(entity);
        AdjustVisibleLiveProjection(location.Key, entity, -1);
    }

    private void RemoveLoadedLandblockFromFlatView(LoadedLandblock landblock)
    {
        foreach (WorldEntity entity in landblock.Entities)
        {
            RemoveFlatEntity(entity);
            if (entity.ServerGuid != 0)
            {
                RuntimeEntityKey key = _projectionLocations[entity].Key;
                RemoveProjectionLocation(entity);
                AdjustVisibleLiveProjection(key, entity, -1);
            }
        }
    }

    private void AddLoadedLandblockToFlatView(LoadedLandblock landblock)
    {
        for (int i = 0; i < landblock.Entities.Count; i++)
        {
            WorldEntity entity = landblock.Entities[i];
            AddFlatEntity(entity);
            if (entity.ServerGuid != 0)
                SetProjectionLocation(entity, landblock.LandblockId, isLoaded: true, i);
            if (entity.ServerGuid != 0)
            {
                AdjustVisibleLiveProjection(
                    _projectionLocations[entity].Key,
                    entity,
                    +1);
            }
        }
    }

    public void AddLandblock(
        LoadedLandblock landblock,
        IEnumerable<ulong>? additionalRenderIds = null,
        LandblockStreamTier tier = LandblockStreamTier.Near)
    {
        using MutationBatch mutation = BeginMutationBatch();
        GpuLandblockSpatialPublication publication =
            CommitLandblockSpatialCore(landblock, additionalRenderIds, tier);
        ActivateLandblockPresentation(publication);
    }

    internal GpuLandblockSpatialPublication CommitLandblockSpatial(
        LoadedLandblock landblock,
        IEnumerable<ulong>? additionalRenderIds,
        LandblockStreamTier tier,
        IEnumerable<ulong>? additionalOrdinaryRenderIds = null) =>
        CommitLandblockSpatialCore(
            landblock,
            additionalRenderIds,
            tier,
            additionalOrdinaryRenderIds);

    private GpuLandblockSpatialPublication CommitLandblockSpatialCore(
        LoadedLandblock landblock,
        IEnumerable<ulong>? additionalRenderIds,
        LandblockStreamTier tier,
        IEnumerable<ulong>? additionalOrdinaryRenderIds = null)
    {
        ArgumentNullException.ThrowIfNull(landblock);

        if (tier == LandblockStreamTier.Far && IsNearTier(landblock.LandblockId))
        {
            LoadedLandblock retained = _loaded[landblock.LandblockId];
            return new GpuLandblockSpatialPublication(
                retained.LandblockId,
                retained,
                Array.Empty<ulong>(),
                Array.Empty<WorldEntity>(),
                RequiresActivation: false,
                RenderTraversalOrder:
                    GetRenderTraversalOrder(retained.LandblockId));
        }

        landblock = NormalizeLoadedLandblock(landblock);
        if (_loaded.TryGetValue(landblock.LandblockId, out LoadedLandblock? displaced))
            ReconcileRetainedStaticEntityScripts(displaced, landblock);

        // If pending live entities have been waiting for this landblock,
        // merge them into the render-thread-owned mutable entity bucket before
        // storing. Subsequent live spawns append to that bucket in O(1).
        if (_pendingByLandblock.TryGetValue(landblock.LandblockId, out var pending) && pending.Count > 0)
        {
            var merged = new List<WorldEntity>(landblock.Entities.Count + pending.Count);
            merged.AddRange(landblock.Entities);
            merged.AddRange(pending);
            landblock = NormalizeLoadedLandblock(landblock, merged);
            _pendingByLandblock.Remove(landblock.LandblockId);
        }
        HashSet<ulong>? mergedRenderIds = additionalRenderIds is null
            ? null
            : new HashSet<ulong>(additionalRenderIds);
        if (_pendingRenderIdsByLandblock.Remove(landblock.LandblockId, out var pendingRenderIds))
        {
            mergedRenderIds ??= new HashSet<ulong>();
            mergedRenderIds.UnionWith(pendingRenderIds);
        }

        bool pendingNear = _pendingNearTierLandblocks.Remove(landblock.LandblockId);
        if (pendingNear
            || (_tierByLandblock.TryGetValue(landblock.LandblockId, out var currentTier)
                && currentTier == LandblockStreamTier.Near))
        {
            tier = LandblockStreamTier.Near;
        }

        bool replacesMeshRegistration = false;
        if (RemoveLoadedLandblock(landblock.LandblockId, out displaced))
        {
            replacesMeshRegistration = true;
            RemoveLoadedLandblockFromFlatView(displaced);
            _animatedIndexByLandblock.Remove(landblock.LandblockId);
        }

        AddLoadedLandblock(landblock.LandblockId, landblock);
        AddLoadedLandblockToFlatView(landblock);
        _tierByLandblock[landblock.LandblockId] = tier;
        return CreateSpatialPublication(
            _loaded[landblock.LandblockId],
            (IEnumerable<ulong>?)mergedRenderIds ?? Array.Empty<ulong>(),
            GetRenderTraversalOrder(landblock.LandblockId),
            additionalOrdinaryRenderIds,
            replacesMeshRegistration);
    }

    private void ReconcileRetainedStaticEntityScripts(
        LoadedLandblock displaced,
        LoadedLandblock replacement)
    {
        if (_entityScriptActivator is null)
            return;

        var replacementsById = new Dictionary<uint, WorldEntity>();
        for (int i = 0; i < replacement.Entities.Count; i++)
        {
            WorldEntity entity = replacement.Entities[i];
            if (entity.ServerGuid == 0)
                replacementsById.Add(entity.Id, entity);
        }

        for (int i = 0; i < displaced.Entities.Count; i++)
        {
            WorldEntity prior = displaced.Entities[i];
            if (prior.ServerGuid != 0)
                continue;
            if (replacementsById.TryGetValue(prior.Id, out WorldEntity? retained))
                _entityScriptActivator.OnRebindStatic(prior, retained);
            else
                _entityScriptActivator.OnRemove(prior);
        }
    }

    internal void ActivateLandblockPresentation(
        GpuLandblockSpatialPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!publication.RequiresActivation)
            return;
        if (!_loaded.TryGetValue(publication.LandblockId, out LoadedLandblock? current)
            || !ReferenceEquals(current, publication.Landblock))
        {
            throw new InvalidOperationException(
                $"Landblock 0x{publication.LandblockId:X8} changed before presentation activation.");
        }

        _wbSpawnAdapter?.OnLandblockLoaded(
            publication.Landblock,
            publication.AdditionalRenderIds,
            publication.AdditionalOrdinaryRenderIds,
            publication.ReplacesMeshRegistration);

        // C.1.5b: fire DefaultScript for dat-hydrated entities only.
        // EntityScriptActivator owns a retryable per-static acquisition ledger,
        // so replaying this activation suffix after a later owner failure does
        // not replay already-started defaults.
        if (_entityScriptActivator is not null)
        {
            for (int i = 0; i < publication.StaticEntities.Count; i++)
                _entityScriptActivator.OnCreate(publication.StaticEntities[i]);
        }
    }

    private static GpuLandblockSpatialPublication CreateSpatialPublication(
        LoadedLandblock landblock,
        IEnumerable<ulong> additionalRenderIds,
        uint renderTraversalOrder,
        IEnumerable<ulong>? additionalOrdinaryRenderIds = null,
        bool replacesMeshRegistration = false)
    {
        ulong[] renderIds = additionalRenderIds as ulong[]
            ?? additionalRenderIds.ToArray();
        ulong[] ordinaryRenderIds = additionalOrdinaryRenderIds as ulong[]
            ?? additionalOrdinaryRenderIds?.ToArray()
            ?? Array.Empty<ulong>();
        WorldEntity[] staticEntities = landblock.Entities
            .Where(static entity => entity.ServerGuid == 0)
            .ToArray();
        return new GpuLandblockSpatialPublication(
            landblock.LandblockId,
            landblock,
            renderIds,
            staticEntities,
            RenderTraversalOrder: renderTraversalOrder,
            AdditionalOrdinaryRenderIds: ordinaryRenderIds,
            ReplacesMeshRegistration: replacesMeshRegistration);
    }

    public void MarkPersistent(uint serverGuid)
    {
        _persistentGuids.Add(serverGuid);
    }

    public void RebucketLiveEntity(WorldEntity entity, uint newCanonicalLb)
    {
        ArgumentNullException.ThrowIfNull(entity);
        RuntimeEntityKey key = _projectionLocations.TryGetValue(
                entity,
                out ProjectionLocation existing)
            ? existing.Key
            : new RuntimeEntityKey(entity.Id, 0);
        RebucketLiveEntity(key, entity, newCanonicalLb);
    }

    public void RebucketLiveEntity(
        RuntimeEntityKey key,
        WorldEntity entity,
        uint newCanonicalLb)
    {
        if (entity.ServerGuid == 0) return;
        if (key.LocalEntityId == 0 || key.LocalEntityId != entity.Id)
            throw new InvalidOperationException(
                "The exact rebucket key must match the WorldEntity local ID.");

        using MutationBatch mutation = BeginMutationBatch();

        uint canonical = (newCanonicalLb & 0xFFFF0000u) | 0xFFFFu;

        bool hasCurrent = _projectionLocations.TryGetValue(
            entity,
            out ProjectionLocation current);
        if (hasCurrent && current.Key != key)
        {
            throw new InvalidOperationException(
                "The live spatial projection is owned by another Runtime incarnation.");
        }
        if (hasCurrent
            && current.IsLoaded
            && current.LandblockId == canonical)
        {
            return;
        }

        RemoveEntityFromAllBuckets(entity);
        PlaceLiveEntityProjection(key, canonical, entity);
    }

    private void RemoveEntityFromAllBuckets(WorldEntity entity)
    {
        if (!_projectionLocations.TryGetValue(entity, out ProjectionLocation location))
            return;

        if (location.IsLoaded)
        {
            RemoveLoadedProjection(entity);
            return;
        }

        if (!_pendingByLandblock.TryGetValue(location.LandblockId, out List<WorldEntity>? pending)
            || (uint)location.BucketIndex >= (uint)pending.Count
            || !ReferenceEquals(pending[location.BucketIndex], entity))
        {
            throw new InvalidOperationException(
                $"Indexed live entity 0x{entity.Id:X8} was absent from pending landblock 0x{location.LandblockId:X8}.");
        }

        int lastIndex = pending.Count - 1;
        if (location.BucketIndex != lastIndex)
        {
            WorldEntity moved = pending[lastIndex];
            pending[location.BucketIndex] = moved;
            SetProjectionLocation(
                moved,
                location.LandblockId,
                isLoaded: false,
                location.BucketIndex);
        }
        pending.RemoveAt(lastIndex);

        RemoveProjectionLocation(entity);
        if (pending.Count == 0)
            _pendingByLandblock.Remove(location.LandblockId);
    }

    public GpuLandblockRetirement? DetachLandblock(uint landblockId)
    {
        using MutationBatch mutation = BeginMutationBatch();

        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        bool hadState = _loaded.ContainsKey(canonical)
            || _pendingByLandblock.ContainsKey(canonical)
            || _pendingRenderIdsByLandblock.ContainsKey(canonical)
            || _pendingNearTierLandblocks.Contains(canonical)
            || _tierByLandblock.ContainsKey(canonical)
            || _aabbs.ContainsKey(canonical);
        if (!hadState)
            return null;

        IReadOnlyList<WorldEntity> detachedEntities =
            _loaded.TryGetValue(canonical, out LoadedLandblock? residentLandblock)
                ? residentLandblock.Entities
                : Array.Empty<WorldEntity>();
        if (_pendingByLandblock.TryGetValue(canonical, out List<WorldEntity>? detachedPending)
            && detachedPending.Count > 0)
        {
            if (detachedEntities.Count == 0)
            {
                detachedEntities = detachedPending;
            }
            else
            {
                var combined = new List<WorldEntity>(
                    detachedEntities.Count + detachedPending.Count);
                var seen = new HashSet<WorldEntity>(ReferenceEqualityComparer.Instance);
                foreach (WorldEntity entity in detachedEntities)
                    if (seen.Add(entity))
                        combined.Add(entity);
                foreach (WorldEntity entity in detachedPending)
                    if (seen.Add(entity))
                        combined.Add(entity);
                detachedEntities = combined;
            }
        }

        var retainedLive = new List<WorldEntity>();
        var retainedLiveSet = new HashSet<WorldEntity>(ReferenceEqualityComparer.Instance);
        var retainedLiveKeys = new Dictionary<WorldEntity, RuntimeEntityKey>(
            ReferenceEqualityComparer.Instance);
        void RetainOrRescue(WorldEntity entity, string source)
        {
            if (entity.ServerGuid == 0)
                return;
            if (!_projectionLocations.TryGetValue(entity, out ProjectionLocation location))
            {
                throw new InvalidOperationException(
                    $"Live projection 0x{entity.Id:X8} has no exact spatial owner.");
            }
            if (_persistentGuids.Contains(entity.ServerGuid))
            {
                _persistentRescued.Add(entity);
                EntityVanishProbe.Log(
                    $"[ent] RESCUE guid=0x{entity.ServerGuid:X8} from={source} lb=0x{canonical:X8}");
                return;
            }
            if (retainedLiveSet.Add(entity))
            {
                retainedLive.Add(entity);
                retainedLiveKeys.Add(entity, location.Key);
            }
        }

        if (_loaded.TryGetValue(canonical, out var lb))
        {
            foreach (var entity in lb.Entities)
                RetainOrRescue(entity, "loaded");
        }

        if (_pendingByLandblock.TryGetValue(canonical, out var pendingForLb))
        {
            foreach (var entity in pendingForLb)
            {
                RetainOrRescue(entity, "pending");
                RemoveProjectionLocation(entity);
            }
        }

        _pendingByLandblock.Remove(canonical);
        _pendingRenderIdsByLandblock.Remove(canonical);
        _pendingNearTierLandblocks.Remove(canonical);
        if (retainedLive.Count > 0)
            _pendingByLandblock[canonical] = retainedLive;
        _aabbs.Remove(canonical);
        _animatedIndexByLandblock.Remove(canonical);

        _tierByLandblock.Remove(canonical);
        if (RemoveLoadedLandblock(canonical, out LoadedLandblock? removed))
            RemoveLoadedLandblockFromFlatView(removed);

        for (int i = 0; i < retainedLive.Count; i++)
        {
            WorldEntity entity = retainedLive[i];
            SetProjectionLocation(
                entity,
                canonical,
                isLoaded: false,
                i,
                retainedLiveKeys[entity]);
        }

        return new GpuLandblockRetirement(
            canonical,
            LandblockRetirementKind.Full,
            detachedEntities);
    }

    internal GpuWorldRecenterRetirement DetachAllForOriginRecenter()
    {
        var ids = new List<uint>(
            _loaded.Count
            + _pendingByLandblock.Count
            + _pendingRenderIdsByLandblock.Count);
        var seenIds = new HashSet<uint>();

        void AddId(uint id)
        {
            uint canonical = (id & 0xFFFF0000u) | 0xFFFFu;
            if (seenIds.Add(canonical))
                ids.Add(canonical);
        }

        // Preserve the accepted renderer traversal order for already-resident
        // landblocks, then append presentation-only and pending-only owners in
        // their stable insertion order.
        for (int i = 0; i < _renderTraversalLandblockSlots.Count; i++)
        {
            uint id = _renderTraversalLandblockSlots[i];
            if (id != 0u)
                AddId(id);
        }
        foreach (uint id in _pendingRenderIdsByLandblock.Keys)
            AddId(id);
        foreach (uint id in _pendingNearTierLandblocks)
            AddId(id);
        foreach (uint id in _tierByLandblock.Keys)
            AddId(id);
        foreach (uint id in _aabbs.Keys)
            AddId(id);

        var retirements = new List<GpuLandblockRetirement>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            uint id = ids[i];
            IReadOnlyList<WorldEntity> loaded =
                _loaded.TryGetValue(id, out LoadedLandblock? landblock)
                    ? landblock.Entities
                    : Array.Empty<WorldEntity>();
            IReadOnlyList<WorldEntity> pending =
                _pendingByLandblock.TryGetValue(id, out List<WorldEntity>? bucket)
                    ? bucket
                    : Array.Empty<WorldEntity>();
            IReadOnlyList<WorldEntity> detached = loaded;
            if (pending.Count != 0)
            {
                if (loaded.Count == 0)
                {
                    detached = pending;
                }
                else
                {
                    var combined = new List<WorldEntity>(
                        loaded.Count + pending.Count);
                    var seenEntities = new HashSet<WorldEntity>(
                        ReferenceEqualityComparer.Instance);
                    for (int entityIndex = 0;
                         entityIndex < loaded.Count;
                         entityIndex++)
                    {
                        WorldEntity entity = loaded[entityIndex];
                        if (seenEntities.Add(entity))
                            combined.Add(entity);
                    }
                    for (int entityIndex = 0;
                         entityIndex < pending.Count;
                         entityIndex++)
                    {
                        WorldEntity entity = pending[entityIndex];
                        if (seenEntities.Add(entity))
                            combined.Add(entity);
                    }
                    detached = combined;
                }
            }

            retirements.Add(new GpuLandblockRetirement(
                id,
                LandblockRetirementKind.Full,
                detached));
        }

        var retainedByLandblock =
            new Dictionary<uint, List<(
                WorldEntity Entity,
                int BucketIndex,
                RuntimeEntityKey Key)>>();
        var rescued = new HashSet<WorldEntity>(
            _persistentRescued,
            ReferenceEqualityComparer.Instance);
        foreach ((WorldEntity entity, ProjectionLocation location) in
                 _projectionLocations)
        {
            if (_persistentGuids.Contains(entity.ServerGuid))
            {
                if (rescued.Add(entity))
                {
                    _persistentRescued.Add(entity);
                    EntityVanishProbe.Log(
                        $"[ent] RESCUE guid=0x{entity.ServerGuid:X8} " +
                        $"from=recenter lb=0x{location.LandblockId:X8}");
                }
                continue;
            }

            if (!retainedByLandblock.TryGetValue(
                    location.LandblockId,
                    out List<(
                        WorldEntity Entity,
                        int BucketIndex,
                        RuntimeEntityKey Key)>? retained))
            {
                retained = [];
                retainedByLandblock.Add(location.LandblockId, retained);
            }
            retained.Add((entity, location.BucketIndex, location.Key));
        }

        int spatialOperationCount = OriginRecenterSpatialOperationCount;
        Exception? observerFailure = null;
        MutationBatch mutation = BeginMutationBatch();
        try
        {
            foreach ((RuntimeEntityKey key, int count) in _visibleLiveProjectionCounts)
            {
                if (count > 0
                    && !_visibilityBeforeMutation.ContainsKey(key))
                {
                    _visibilityBeforeMutation.Add(key, true);
                }
            }
            _visibleLiveProjectionCounts.Clear();

            if (_flatEntities.Count != 0)
                _flatMembershipDirty = true;
            _flatEntities.Clear();
            _flatEntityIndices.Clear();
            _projectionLocations.Clear();
            _loadedLiveByLandblock.Clear();
            _liveProjectionByKey.Clear();

            if (_loaded.Count != 0)
                _residentWindowRevision = checked(_residentWindowRevision + 1);
            _loaded.Clear();
            _renderTraversalLandblockSlots.Clear();
            _freeRenderTraversalLandblockSlots.Clear();
            _renderTraversalSlotByLandblock.Clear();
            _tierByLandblock.Clear();
            _aabbs.Clear();
            _animatedIndexByLandblock.Clear();
            _pendingByLandblock.Clear();
            _pendingRenderIdsByLandblock.Clear();
            _pendingNearTierLandblocks.Clear();

            _landblockEntriesView.Clear();
            _landblockEntriesWithoutAnimatedIndexView.Clear();
            _landblockBoundsView.Clear();
            InvalidateLandblockRenderViews(entries: true, bounds: true);

            foreach ((uint id, List<(
                         WorldEntity Entity,
                         int BucketIndex,
                         RuntimeEntityKey Key)> retained) in
                     retainedByLandblock)
            {
                retained.Sort(static (left, right) =>
                    left.BucketIndex.CompareTo(right.BucketIndex));
                var pending = new List<WorldEntity>(retained.Count);
                for (int i = 0; i < retained.Count; i++)
                {
                    WorldEntity entity = retained[i].Entity;
                    pending.Add(entity);
                    SetProjectionLocation(
                        entity,
                        id,
                        isLoaded: false,
                        i,
                        retained[i].Key);
                }
                _pendingByLandblock.Add(id, pending);
            }
        }
        finally
        {
            try
            {
                mutation.Dispose();
            }
            catch (Exception error)
            {
                observerFailure = error;
            }
        }

        return new GpuWorldRecenterRetirement(
            retirements,
            spatialOperationCount,
            observerFailure);
    }

    public void RemoveLandblock(uint landblockId)
    {
        GpuLandblockRetirement? retirement = DetachLandblock(landblockId);
        if (retirement is null)
            return;

        ReleaseLandblockMeshReferences(retirement.LandblockId);
        InvalidateLandblockClassification(retirement.LandblockId);
        for (int i = 0; i < retirement.Entities.Count; i++)
        {
            WorldEntity entity = retirement.Entities[i];
            if (entity.ServerGuid == 0)
                StopStaticEntityScript(entity);
        }
    }

    internal void ReleaseLandblockMeshReferences(uint landblockId)
    {
        if (_wbSpawnAdapter is null)
            return;

        _wbSpawnAdapter.OnLandblockUnloaded(landblockId);

        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        if (!_loaded.TryGetValue(canonical, out LoadedLandblock? retained))
            return;
        if (!_tierByLandblock.TryGetValue(canonical, out LandblockStreamTier tier)
            || tier != LandblockStreamTier.Far)
        {
            return;
        }

        _wbSpawnAdapter.OnLandblockLoaded(retained);
    }

    internal void InvalidateLandblockClassification(uint landblockId) =>
        _onLandblockUnloaded?.Invoke(landblockId);

    internal void StopStaticEntityScript(WorldEntity entity) =>
        _entityScriptActivator?.OnRemove(entity);

    private readonly List<WorldEntity> _persistentRescued = new();

    public List<WorldEntity> DrainRescued()
    {
        if (_persistentRescued.Count == 0) return _persistentRescued;
        var result = new List<WorldEntity>(_persistentRescued);
        _persistentRescued.Clear();
        return result;
    }

    public void RemoveLiveEntityProjection(uint serverGuid)
    {
        if (serverGuid == 0) return;

        using MutationBatch mutation = BeginMutationBatch();

        _guidRemovalScratch.Clear();
        foreach (WorldEntity projection in _projectionLocations.Keys)
        {
            if (projection.ServerGuid == serverGuid)
                _guidRemovalScratch.Add(projection);
        }
        for (int i = 0; i < _guidRemovalScratch.Count; i++)
            RemoveEntityFromAllBuckets(_guidRemovalScratch[i]);
        _guidRemovalScratch.Clear();

        // A persistent projection may have been rescued by RemoveLandblock
        // and not yet drained by the next GameWindow frame. Rebucketing or
        // logical teardown before that drain must remove the stale rescue
        // reference or it can later re-inject a deleted/duplicated object.
        _persistentRescued.RemoveAll(e => e.ServerGuid == serverGuid);

    }

    public void RemoveLiveEntityProjection(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.ServerGuid == 0) return;

        using MutationBatch mutation = BeginMutationBatch();

        RemoveEntityFromAllBuckets(entity);

        _persistentRescued.RemoveAll(candidate => ReferenceEquals(candidate, entity));

    }

    public void ForgetLiveEntity(uint serverGuid)
    {
        RemoveLiveEntityProjection(serverGuid);
        _persistentGuids.Remove(serverGuid);
        _persistentInFlatProbe.Remove(serverGuid);
    }

    public void ClearLiveEntityLifetimeState()
    {
        _persistentGuids.Clear();
        _persistentRescued.Clear();
        _persistentInFlatProbe.Clear();
        _visibilityTransitions.Clear();
        _visibleLiveProjectionCounts.Clear();
        _visibilityBeforeMutation.Clear();
        _projectionLocations.Clear();
        _loadedLiveByLandblock.Clear();
        _liveProjectionByKey.Clear();
    }

    public void PlaceLiveEntityProjection(uint landblockId, WorldEntity entity) =>
        PlaceLiveEntityProjection(
            new RuntimeEntityKey(entity.Id, 0),
            landblockId,
            entity);

    public void PlaceLiveEntityProjection(
        RuntimeEntityKey key,
        uint landblockId,
        WorldEntity entity)
    {
        using MutationBatch mutation = BeginMutationBatch();

        if (key.LocalEntityId == 0 || key.LocalEntityId != entity.Id)
            throw new InvalidOperationException(
                "The exact placement key must match the WorldEntity local ID.");
        if (_projectionLocations.ContainsKey(entity))
        {
            throw new InvalidOperationException(
                $"Live entity 0x{entity.Id:X8} is already spatially projected.");
        }

        // Spatial placement only. LiveEntityRuntime has already registered
        // this incarnation's renderer and script resources exactly once.
        uint canonicalLandblockId = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        bool probePersistent = EntityVanishProbe.Enabled
            && entity.ServerGuid != 0 && _persistentGuids.Contains(entity.ServerGuid);

        if (_loaded.ContainsKey(canonicalLandblockId))
        {
            // Hot path — append directly to the render-thread-owned mutable
            // resident bucket and flat view.
            AddLoadedProjection(canonicalLandblockId, entity, key);
            if (probePersistent)
                EntityVanishProbe.Log($"[ent] APPEND guid=0x{entity.ServerGuid:X8} lb=0x{canonicalLandblockId:X8} -> LOADED(drawn)");
            return;
        }

        if (!_pendingByLandblock.TryGetValue(canonicalLandblockId, out var bucket))
        {
            bucket = new List<WorldEntity>();
            _pendingByLandblock[canonicalLandblockId] = bucket;
        }
        int bucketIndex = bucket.Count;
        bucket.Add(entity);
        SetProjectionLocation(
            entity,
            canonicalLandblockId,
            isLoaded: false,
            bucketIndex,
            key);
        if (probePersistent)
            EntityVanishProbe.Log($"[ent] APPEND guid=0x{entity.ServerGuid:X8} lb=0x{canonicalLandblockId:X8} -> PENDING(hidden)");
    }

    public GpuLandblockRetirement? DetachNearLayer(uint landblockId)
    {
        using MutationBatch mutation = BeginMutationBatch();

        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        bool hadNearLayer = _pendingNearTierLandblocks.Remove(canonical);
        hadNearLayer |= _pendingRenderIdsByLandblock.Remove(canonical);
        hadNearLayer |= IsNearTier(canonical);
        if (!hadNearLayer)
            return null;

        var retiredEntities = new List<WorldEntity>();
        if (_pendingByLandblock.TryGetValue(canonical, out var pending))
        {
            int pendingWriteIndex = 0;
            for (int readIndex = 0; readIndex < pending.Count; readIndex++)
            {
                WorldEntity entity = pending[readIndex];
                if (entity.ServerGuid == 0)
                {
                    retiredEntities.Add(entity);
                    continue;
                }
                if (pendingWriteIndex != readIndex)
                    pending[pendingWriteIndex] = entity;
                SetProjectionLocation(
                    entity,
                    canonical,
                    isLoaded: false,
                    pendingWriteIndex);
                pendingWriteIndex++;
            }
            if (pendingWriteIndex < pending.Count)
                pending.RemoveRange(pendingWriteIndex, pending.Count - pendingWriteIndex);
            if (pendingWriteIndex == 0)
                _pendingByLandblock.Remove(canonical);
        }

        if (_loaded.TryGetValue(canonical, out var lb))
        {


        var retainedLive = new List<WorldEntity>();
        for (int readIndex = 0; readIndex < lb.Entities.Count; readIndex++)
        {
            WorldEntity entity = lb.Entities[readIndex];
            if (entity.ServerGuid != 0)
            {
                int liveWriteIndex = retainedLive.Count;
                retainedLive.Add(entity);
                SetProjectionLocation(
                    entity,
                    canonical,
                    isLoaded: true,
                    liveWriteIndex);
                continue;
            }

            retiredEntities.Add(entity);
            RemoveFlatEntity(entity);
        }
        _loaded[canonical] = NormalizeLoadedLandblock(lb, retainedLive);
        _animatedIndexByLandblock.Remove(canonical);
        InvalidateLandblockRenderViews(entries: true, bounds: false);
        _tierByLandblock[canonical] = LandblockStreamTier.Far;
        }

        return new GpuLandblockRetirement(
            canonical,
            LandblockRetirementKind.NearLayer,
            retiredEntities);
    }

    public void RemoveEntitiesFromLandblock(uint landblockId)
    {
        GpuLandblockRetirement? retirement = DetachNearLayer(landblockId);
        if (retirement is null)
            return;

        ReleaseLandblockMeshReferences(retirement.LandblockId);
        InvalidateLandblockClassification(retirement.LandblockId);
        for (int i = 0; i < retirement.Entities.Count; i++)
            StopStaticEntityScript(retirement.Entities[i]);
    }

    public bool AddEntitiesToExistingLandblock(
        uint landblockId,
        IReadOnlyList<WorldEntity> entities,
        IEnumerable<ulong>? additionalRenderIds = null)
    {
        using MutationBatch mutation = BeginMutationBatch();
        GpuLandblockSpatialPublication? publication =
            CommitEntitiesToExistingLandblockSpatialCore(
                landblockId,
                entities,
                additionalRenderIds,
                additionalOrdinaryRenderIds: null,
                parkIfMissing: true);
        if (publication is null)
            return false;

        ActivateLandblockPresentation(publication);
        return true;
    }

    internal GpuLandblockSpatialPublication CommitEntitiesToExistingLandblockSpatial(
        uint landblockId,
        IReadOnlyList<WorldEntity> entities,
        IEnumerable<ulong>? additionalRenderIds,
        IEnumerable<ulong>? additionalOrdinaryRenderIds = null)
    {
        GpuLandblockSpatialPublication? publication =
            CommitEntitiesToExistingLandblockSpatialCore(
                landblockId,
                entities,
                additionalRenderIds,
                additionalOrdinaryRenderIds,
                parkIfMissing: false);
        return publication
            ?? throw new InvalidOperationException(
                $"Landblock 0x{landblockId:X8} is not resident for an accepted promotion.");
    }

    private GpuLandblockSpatialPublication? CommitEntitiesToExistingLandblockSpatialCore(
        uint landblockId,
        IReadOnlyList<WorldEntity> entities,
        IEnumerable<ulong>? additionalRenderIds,
        IEnumerable<ulong>? additionalOrdinaryRenderIds,
        bool parkIfMissing)
    {
        ArgumentNullException.ThrowIfNull(entities);

        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        if (!_loaded.TryGetValue(canonical, out var lb))
        {
            if (!parkIfMissing)
                return null;

            // Park as pending — same pattern as live projections for not-yet-loaded LBs.
            if (!_pendingByLandblock.TryGetValue(canonical, out var bucket))
            {
                bucket = new List<WorldEntity>();
                _pendingByLandblock[canonical] = bucket;
            }
            int firstIndex = bucket.Count;
            bucket.AddRange(entities);
            for (int i = 0; i < entities.Count; i++)
            {
                WorldEntity entity = entities[i];
                if (entity.ServerGuid != 0)
                {
                    SetProjectionLocation(
                        entity,
                        canonical,
                        isLoaded: false,
                        firstIndex + i);
                }
            }
            _pendingNearTierLandblocks.Add(canonical);
            if (additionalRenderIds is not null)
            {
                if (!_pendingRenderIdsByLandblock.TryGetValue(canonical, out var renderIds))
                {
                    renderIds = new HashSet<ulong>();
                    _pendingRenderIdsByLandblock[canonical] = renderIds;
                }
                renderIds.UnionWith(additionalRenderIds);
            }
            return null;
        }
        List<WorldEntity> resident = MutableEntities(lb);
        for (int i = 0; i < entities.Count; i++)
        {
            WorldEntity entity = entities[i];
            int bucketIndex = resident.Count;
            resident.Add(entity);
            AddFlatEntity(entity);
            if (entity.ServerGuid != 0)
            {
                SetProjectionLocation(entity, canonical, isLoaded: true, bucketIndex);
                AdjustVisibleLiveProjection(
                    _projectionLocations[entity].Key,
                    entity,
                    +1);
            }
        }
        _animatedIndexByLandblock.Remove(canonical);
        InvalidateLandblockRenderViews(entries: true, bounds: false);
        _tierByLandblock[canonical] = LandblockStreamTier.Near;
        ulong[] effectiveRenderIds = additionalRenderIds?.ToArray() ?? Array.Empty<ulong>();
        WorldEntity[] staticEntities = entities
            .Where(static entity => entity.ServerGuid == 0)
            .ToArray();
        return new GpuLandblockSpatialPublication(
            canonical,
            _loaded[canonical],
            effectiveRenderIds,
            staticEntities,
            RenderTraversalOrder: GetRenderTraversalOrder(canonical),
            AdditionalOrdinaryRenderIds:
                additionalOrdinaryRenderIds?.ToArray() ?? Array.Empty<ulong>());
    }

    private void DrainVisibilityTransitions()
    {
        if (_dispatchingVisibilityTransitions)
            return;

        List<Exception>? failures = null;
        _dispatchingVisibilityTransitions = true;
        try
        {
            while (_visibilityTransitions.TryDequeue(out var transition))
            {
                Delegate[] subscribers = LiveProjectionVisibilityChanged?
                    .GetInvocationList()
                    ?? Array.Empty<Delegate>();
                for (int i = 0; i < subscribers.Length; i++)
                {
                    try
                    {
                        ((Action<RuntimeEntityKey, bool>)subscribers[i])(
                            transition.Key,
                            transition.Visible);
                    }
                    catch (Exception error)
                    {
                        (failures ??= new List<Exception>()).Add(error);
                    }
                }
            }
        }
        finally
        {
            _dispatchingVisibilityTransitions = false;
        }

        if (failures is not null)
        {
            throw new AggregateException(
                "One or more live projection visibility observers failed.",
                failures);
        }
    }

    private readonly HashSet<uint> _persistentInFlatProbe = new();

    private void ProbeFlatViewTransitions()
    {
        var now = new HashSet<uint>();
        foreach (var e in _flatEntities)
            if (e.ServerGuid != 0 && _persistentGuids.Contains(e.ServerGuid))
                now.Add(e.ServerGuid);
        foreach (var g in now)
            if (!_persistentInFlatProbe.Contains(g))
                EntityVanishProbe.Log($"[ent] DRAWSET guid=0x{g:X8} -> PRESENT (flatCount={_flatEntities.Count})");
        foreach (var g in _persistentInFlatProbe)
            if (!now.Contains(g))
                EntityVanishProbe.Log($"[ent] DRAWSET guid=0x{g:X8} -> ABSENT (flatCount={_flatEntities.Count})");
        _persistentInFlatProbe.Clear();
        foreach (var g in now) _persistentInFlatProbe.Add(g);
    }
}
