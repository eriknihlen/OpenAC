using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Items;
using AcDream.Core.World.Cells;

namespace AcDream.Core.Physics;

internal readonly record struct TerrainWalkableSample(
    System.Numerics.Plane Plane,
    TerrainTriangleVertices Vertices,
    float WaterDepth,
    bool IsWater,
    uint CellId);

public sealed class PhysicsEngine
{
    private CollisionWorldStateSlot _collisionWorld;
    private Dictionary<uint, LandblockPhysics> _landblocks =>
        _collisionWorld.Current.Landblocks;
    private List<uint> _landblockSlots =>
        _collisionWorld.Current.LandblockSlots;
    private Dictionary<uint, int> _landblockIndices =>
        _collisionWorld.Current.LandblockIndices;
    private Stack<int> _landblockFreeSlots =>
        _collisionWorld.Current.LandblockFreeSlots;
    private readonly TransitionScratchArena? _transitionScratch;

    private readonly HashSet<uint> _terrainResidencyScratch = new();

    public PhysicsEngine()
        : this(reuseTransitionScratch: true)
    {
    }

    internal PhysicsEngine(bool reuseTransitionScratch)
    {
        _collisionWorld = new CollisionWorldStateSlot();
        ShadowObjects = new ShadowObjectRegistry(_collisionWorld);
        _transitionScratch = reuseTransitionScratch
            ? new TransitionScratchArena()
            : null;
    }

    private Transition RentTransition()
        => _transitionScratch?.Rent() ?? new Transition();

    private void ReturnTransition(Transition transition)
        => _transitionScratch?.Return(transition);

    /// <summary>Number of registered landblocks (diagnostic).</summary>
    public int LandblockCount => _landblocks.Count;

    internal CollisionWorldStateSlot CollisionWorld => _collisionWorld;

    public Action<string>? DiagnosticLog { get; set; }

    internal Func<
        Transition,
        TransitionCellCollisionPhase,
        uint,
        TransitionState,
        TransitionState>? TransitionCellCollisionTestHook { get; set; }

    internal Func<double> SetPositionRandomUnit { get; set; } =
        Random.Shared.NextDouble;

    public bool IsLandblockTerrainResident(uint cellOrLandblockId)
    {
        uint prefix = cellOrLandblockId & 0xFFFF0000u;
        foreach ((uint key, _) in _landblocks)
            if ((key & 0xFFFF0000u) == prefix) return true;
        return false;
    }

    public bool IsNeighborhoodTerrainResident(uint cellOrLandblockId, int radius)
    {
        HashSet<uint> resident = _terrainResidencyScratch;
        resident.Clear();
        foreach ((uint key, _) in _landblocks)
            resident.Add(key & 0xFFFF0000u);

        int cx = (int)((cellOrLandblockId >> 24) & 0xFFu);
        int cy = (int)((cellOrLandblockId >> 16) & 0xFFu);
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || nx > 254 || ny < 0 || ny > 254) continue;   // off-map: skip
                uint prefix = ((uint)nx << 24) | ((uint)ny << 16);
                if (!resident.Contains(prefix)) return false;
            }
        return true;
    }

    public ShadowObjectRegistry ShadowObjects { get; }

    public PhysicsDataCache? DataCache
    {
        get => _dataCache;
        set
        {
            if (value is not null
                && !ReferenceEquals(_collisionWorld, value.CollisionWorld))
            {
                if (ShadowObjects.TotalRegistered != 0)
                {
                    throw new InvalidOperationException(
                        "A populated physics engine cannot change collision roots.");
                }
                if (_dataCache is null && _landblocks.Count != 0)
                    CopyDetachedLandblocksTo(value.CollisionWorld);
                else if (_landblocks.Count != 0)
                    throw new InvalidOperationException(
                        "A populated physics engine cannot change collision roots.");
                _collisionWorld = value.CollisionWorld;
                ShadowObjects.AttachCollisionWorld(_collisionWorld);
            }
            _dataCache = value;
            ShadowObjects.DataCache = value;
        }
    }
    private PhysicsDataCache? _dataCache;

    private void CopyDetachedLandblocksTo(
        CollisionWorldStateSlot destinationSlot)
    {
        CollisionWorldState source = _collisionWorld.Current;
        CollisionWorldState destination = destinationSlot.Current;
        if (destination.Landblocks.Count != 0
            || destination.LandblockSlots.Count != 0
            || destination.LandblockIndices.Count != 0
            || destination.LandblockFreeSlots.Count != 0)
        {
            throw new InvalidOperationException(
                "A cache collision root already owns engine landblocks.");
        }
        foreach ((uint landblockId, LandblockPhysics landblock) in
                 source.Landblocks)
        {
            destination.Landblocks.Add(landblockId, landblock);
        }
        destination.LandblockSlots.AddRange(source.LandblockSlots);
        foreach ((uint landblockId, int slot) in source.LandblockIndices)
            destination.LandblockIndices.Add(landblockId, slot);
        int[] freeSlots = source.LandblockFreeSlots.ToArray();
        for (int index = freeSlots.Length - 1; index >= 0; index--)
            destination.LandblockFreeSlots.Push(freeSlots[index]);
    }

    private ClientObjectTable? _objects;
    private ulong _objectsBindingRevision;

    public ClientObjectTable? Objects
    {
        get => _objects;
        set
        {
            if (ReferenceEquals(_objects, value))
                return;
            _objects = value;
            _objectsBindingRevision = checked(_objectsBindingRevision + 1UL);
        }
    }

    internal ulong ObjectsBindingRevision => _objectsBindingRevision;

    internal sealed record LandblockPhysics(
        TerrainSurface Terrain,
        IReadOnlyList<CellSurface> Cells,
        IReadOnlyList<PortalPlane> Portals,
        float WorldOffsetX,
        float WorldOffsetY);

    internal CollisionStagingBuilder CreateCollisionStagingBuilder(
        uint targetLandblockId)
    {
        _ = targetLandblockId;
        PhysicsDataCache activeCache = DataCache
            ?? throw new InvalidOperationException(
                "Active collision engine has no data cache.");
        return new CollisionStagingBuilder(this, activeCache);
    }

    internal LandblockReplacementBuilder CreateLandblockReplacementBuilder(
        PhysicsEngine staging,
        uint landblockId,
        uint[] gfxObjectIds,
        uint[] setupIds,
        IReadOnlyList<uint> expectedRetainedOwners)
    {
        ArgumentNullException.ThrowIfNull(staging);
        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        if (!staging._landblocks.TryGetValue(
                canonical,
                out LandblockPhysics? landblock))
        {
            throw new InvalidOperationException(
                $"Staging collision generation has no landblock 0x{canonical:X8}.");
        }
        PhysicsDataCache stagingCache = staging.DataCache
            ?? throw new InvalidOperationException(
                "Staging collision engine has no data cache.");
        return new LandblockReplacementBuilder(
            canonical,
            landblock,
            staging,
            (DataCache ?? throw new InvalidOperationException(
                "Active collision engine has no data cache."))
                .CreateLandblockReplacementBuilder(
                stagingCache,
                canonical,
                gfxObjectIds,
                setupIds),
            ShadowObjects.CreateLandblockReplacementBuilder(
                staging.ShadowObjects,
                canonical,
                expectedRetainedOwners));
    }

    internal void CommitLandblockReplacement(
        PreparedPhysicsEngineLandblock replacement)
    {
        PhysicsDataCache activeCache = DataCache
            ?? throw new InvalidOperationException(
                "Active collision engine has no data cache.");
        PhysicsDataCache stagingCache = replacement.Staging.DataCache
            ?? throw new InvalidOperationException(
                "Staging collision engine has no data cache.");
        ShadowObjectRegistry stagingShadows = replacement.Staging.ShadowObjects;
        CollisionWorldState active = activeCache.CollisionWorld.Current;

        PreparedPhysicsDataCacheLandblock data = replacement.DataCache;
        for (int index = 0; index < data.CellIdsToRemove.Count; index++)
            active.ShadowCells.Remove(data.CellIdsToRemove[index]);
        IReadOnlyList<uint> envCellRemovals =
            data.CellGraph.EnvCellIdsToRemove;
        for (int index = 0; index < envCellRemovals.Count; index++)
            active.ShadowCells.Remove(envCellRemovals[index]);

        using (LandblockReplacementApplyCursor cursor =
            CreateLandblockReplacementApplyCursor(replacement))
        {
            while (true)
            {
                LandblockReplacementApplyStep step = cursor.Advance();
                if (step.HasOwner)
                {
                    ShadowObjects.ApplyCommittedOwnerReplacement(
                        stagingShadows,
                        step.OwnerId,
                        replacement.LandblockId);
                }
                if (step.Completed)
                    break;
            }
        }

        ShadowObjects.RefloodPrefixOwnersAfterReplacement(
            replacement.LandblockId,
            replacement.Shadows.OwnerIds);

        stagingCache.CollisionWorld.Revoke();
    }

    internal LandblockReplacementApplyCursor
        CreateLandblockReplacementApplyCursor(
            PreparedPhysicsEngineLandblock replacement) =>
        new(this, replacement);

    internal readonly record struct LandblockReplacementApplyStep(
        bool Completed,
        bool Worked,
        bool HasOwner,
        uint OwnerId);

    internal sealed class LandblockReplacementApplyCursor : IDisposable
    {
        private readonly PhysicsEngine _destinationEngine;
        private readonly PhysicsDataCache _destinationCache;
        private readonly CollisionWorldState _destination;
        private readonly PreparedPhysicsEngineLandblock _replacement;
        private int _phase;
        private int _index;

        internal LandblockReplacementApplyCursor(
            PhysicsEngine destination,
            PreparedPhysicsEngineLandblock replacement)
        {
            _destinationEngine = destination;
            _destinationCache = destination.DataCache
                ?? throw new InvalidOperationException(
                    "Collision engine has no data cache.");
            _destination = _destinationCache.CollisionWorld.Capture();
            _replacement = replacement;
        }

        internal uint LandblockId => _replacement.LandblockId;

        internal LandblockReplacementApplyStep Advance()
        {
            PreparedPhysicsDataCacheLandblock data = _replacement.DataCache;
            PreparedCellGraphLandblock graph = data.CellGraph;
            while (true)
            {
                switch (_phase)
                {
                    case 0:
                        if (_index < data.CellIdsToRemove.Count)
                        {
                            _destination.RemoveCellStruct(
                                data.CellIdsToRemove[_index++]);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 1:
                        if (_index < data.Cells.Count)
                        {
                            KeyValuePair<uint, CellPhysics> pair =
                                data.Cells[_index++];
                            _destination.SetCellStruct(pair.Key, pair.Value);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 2:
                        if (_index < data.FlatCellIdsToRemove.Count)
                        {
                            _destination.RemoveFlatCellStruct(
                                data.FlatCellIdsToRemove[_index++]);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 3:
                        if (_index < data.FlatCells.Count)
                        {
                            KeyValuePair<uint, FlatCellStructureCollisionAsset>
                                pair = data.FlatCells[_index++];
                            _destination.SetFlatCellStruct(pair.Key, pair.Value);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 4:
                        if (_index < data.FlatEnvCellIdsToRemove.Count)
                        {
                            _destination.RemoveFlatEnvCell(
                                data.FlatEnvCellIdsToRemove[_index++]);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 5:
                        if (_index < data.FlatEnvCells.Count)
                        {
                            KeyValuePair<uint, FlatEnvCellTopology> pair =
                                data.FlatEnvCells[_index++];
                            _destination.SetFlatEnvCell(pair.Key, pair.Value);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 6:
                        if (_index < data.BuildingIdsToRemove.Count)
                        {
                            _destination.RemoveBuilding(
                                data.BuildingIdsToRemove[_index++]);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 7:
                        if (_index < data.Buildings.Count)
                        {
                            KeyValuePair<uint, BuildingPhysics> pair =
                                data.Buildings[_index++];
                            _destination.SetBuilding(pair.Key, pair.Value);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 8:
                        if (_index < graph.EnvCellIdsToRemove.Count)
                        {
                            _destination.RemoveEnvCell(
                                graph.EnvCellIdsToRemove[_index++]);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 9:
                        if (graph.HasTerrain)
                        {
                            if (_index == 0)
                            {
                                _destination.Terrain[graph.LandblockPrefix] =
                                    graph.Terrain!;
                                _index++;
                                return Worked();
                            }
                            if (_index <= 0x40)
                            {
                                uint low = (uint)_index++;
                                CellGraphTerrain terrain = graph.Terrain!;
                                int cellIndex = (int)(low - 1u);
                                uint id = graph.LandblockPrefix | low;
                                _destination.OutdoorCells[id] =
                                    LandCell.Synthesize(
                                        id,
                                        terrain.Terrain,
                                        terrain.Origin,
                                        cellIndex / 8,
                                        cellIndex % 8);
                                return Worked();
                            }
                        }
                        else
                        {
                            if (_index == 0)
                            {
                                _destination.Terrain.TryRemove(
                                    graph.LandblockPrefix,
                                    out _);
                                _index++;
                                return Worked();
                            }
                            if (_index <= 0x40)
                            {
                                uint low = (uint)_index++;
                                _destination.OutdoorCells.TryRemove(
                                    graph.LandblockPrefix | low,
                                    out _);
                                return Worked();
                            }
                        }
                        NextPhase();
                        continue;
                    case 10:
                        if (_index < graph.EnvCells.Count)
                        {
                            KeyValuePair<uint, EnvCell> pair =
                                graph.EnvCells[_index++];
                            _destination.SetEnvCell(pair.Key, pair.Value);
                            return Worked();
                        }
                        NextPhase();
                        continue;
                    case 11:
                        _destinationEngine.InstallLandblockClone(
                            _replacement.LandblockId,
                            _replacement.Landblock);
                        NextPhase();
                        return Worked();
                    case 12:
                        if (_index < _replacement.Shadows.OwnerIds.Count)
                        {
                            uint ownerId =
                                _replacement.Shadows.OwnerIds[_index++];
                            return new LandblockReplacementApplyStep(
                                Completed: false,
                                Worked: true,
                                HasOwner: true,
                                ownerId);
                        }
                        NextPhase();
                        continue;
                    case 13:
                        uint currentCellId =
                            _destinationCache.CellGraph.CurrCell?.Id ?? 0u;
                        if ((currentCellId & 0xFFFF0000u)
                            == graph.LandblockPrefix)
                        {
                            _destinationCache.CellGraph.CurrCell =
                                _destinationCache.CellGraph.GetVisible(
                                    currentCellId);
                        }
                        _phase++;
                        return new LandblockReplacementApplyStep(
                            Completed: true,
                            Worked: false,
                            HasOwner: false,
                            OwnerId: 0u);
                    default:
                        return new LandblockReplacementApplyStep(
                            Completed: true,
                            Worked: false,
                            HasOwner: false,
                            OwnerId: 0u);
                }
            }
        }

        private LandblockReplacementApplyStep Worked() => new(
            Completed: false,
            Worked: true,
            HasOwner: false,
            OwnerId: 0u);

        private void NextPhase()
        {
            _phase++;
            _index = 0;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class CollisionStagingBuilder : IDisposable
    {
        internal CollisionStagingBuilder(
            PhysicsEngine active,
            PhysicsDataCache activeCache)
        {
            var stagingSlot = new CollisionWorldStateSlot();
            StagingCache = activeCache.CreateEmptyCollisionStaging(stagingSlot);
            StagingEngine = new PhysicsEngine
            {
                DataCache = StagingCache,
                Objects = active.Objects,
            };
        }

        internal PhysicsDataCache StagingCache { get; }
        internal PhysicsEngine StagingEngine { get; }

        public void Dispose()
        {
        }
    }

    private void InstallLandblockClone(
        uint landblockId,
        LandblockPhysics landblock)
    {
        _landblocks[landblockId] = landblock;
        EnsureLandblockSlot(landblockId);
    }

    private void EnsureLandblockSlot(uint landblockId)
    {
        if (_landblockIndices.ContainsKey(landblockId))
            return;
        if (_landblockFreeSlots.TryPop(out int freeIndex))
        {
            _landblockSlots[freeIndex] = landblockId;
            _landblockIndices[landblockId] = freeIndex;
            return;
        }
        _landblockIndices[landblockId] = _landblockSlots.Count;
        _landblockSlots.Add(landblockId);
    }

    private void RemoveLandblockSlot(uint landblockId)
    {
        if (!_landblockIndices.Remove(landblockId, out int slotIndex))
            return;
        _landblockSlots[slotIndex] = 0u;
        _landblockFreeSlots.Push(slotIndex);
    }

    internal sealed class PreparedPhysicsEngineLandblock
    {
        internal PreparedPhysicsEngineLandblock(
            uint landblockId,
            LandblockPhysics landblock,
            PhysicsEngine staging,
            PreparedPhysicsDataCacheLandblock dataCache,
            ShadowObjectRegistry.PreparedLandblockShadowReplacement shadows)
        {
            LandblockId = landblockId;
            Landblock = landblock;
            Staging = staging;
            DataCache = dataCache;
            Shadows = shadows;
        }

        internal uint LandblockId { get; }
        internal LandblockPhysics Landblock { get; }
        internal PhysicsEngine Staging { get; }
        internal PreparedPhysicsDataCacheLandblock DataCache { get; }
        internal ShadowObjectRegistry.PreparedLandblockShadowReplacement Shadows { get; }
    }

    internal sealed class LandblockReplacementBuilder : IDisposable
    {
        private readonly uint _landblockId;
        private readonly LandblockPhysics _landblock;
        private readonly PhysicsEngine _staging;
        private readonly PhysicsDataCache.LandblockReplacementBuilder _data;
        private readonly ShadowObjectRegistry.LandblockReplacementBuilder _shadows;
        private int _phase;

        internal LandblockReplacementBuilder(
            uint landblockId,
            LandblockPhysics landblock,
            PhysicsEngine staging,
            PhysicsDataCache.LandblockReplacementBuilder data,
            ShadowObjectRegistry.LandblockReplacementBuilder shadows)
        {
            _landblockId = landblockId;
            _landblock = landblock;
            _staging = staging;
            _data = data;
            _shadows = shadows;
        }

        internal int WorkUnits => _data.WorkUnits + _shadows.WorkUnits;
        internal PreparedPhysicsEngineLandblock? Prepared { get; private set; }

        internal void RefreshRetainedOwner(uint ownerId) =>
            _shadows.RefreshOwner(ownerId);

        internal bool Advance()
        {
            if (_phase == 0)
            {
                if (!_data.Advance())
                    return false;
                _phase++;
                return false;
            }
            if (_phase == 1)
            {
                if (!_shadows.Advance())
                    return false;
                if (_data.Prepared is not null
                    && _shadows.Prepared is not null)
                {
                        Prepared = new PreparedPhysicsEngineLandblock(
                            _landblockId,
                            _landblock,
                            _staging,
                        _data.Prepared,
                        _shadows.Prepared);
                }
                _phase++;
            }
            return true;
        }

        public void Dispose()
        {
            _data.Dispose();
            _shadows.Dispose();
        }
    }

    internal void AddLandblock(uint landblockId, TerrainSurface terrain,
        IReadOnlyList<CellSurface> cells, IReadOnlyList<PortalPlane> portals,
        float worldOffsetX, float worldOffsetY)
    {
        _landblocks[landblockId] = new LandblockPhysics(terrain, cells, portals, worldOffsetX, worldOffsetY);
        EnsureLandblockSlot(landblockId);

        // UCG Stage 1: mirror terrain into the unified graph (inert this stage).
        DataCache?.CellGraph.RegisterTerrain(landblockId, terrain, new Vector3(worldOffsetX, worldOffsetY, 0f));
    }

    /// <summary>
    /// Remove a previously registered landblock, including its shadow objects.
    /// </summary>
    internal void RemoveLandblock(uint landblockId)
    {
        _landblocks.Remove(landblockId);
        RemoveLandblockSlot(landblockId);
        ShadowObjects.DeregisterStaticOwnersForLandblock(landblockId);
        ShadowObjects.RemoveLandblock(landblockId);
        DataCache?.RemoveCellsForLandblock(landblockId);
        DataCache?.RemoveBuildingsForLandblock(landblockId);

        if (DataCache?.CellGraph is { } cg && cg.CurrCell is { } cur
            && (cur.Id & 0xFFFF0000u) == (landblockId & 0xFFFF0000u))
        {
            cg.CurrCell = null;
        }

        // UCG Stage 1: mirror removal into the unified graph (inert this stage).
        DataCache?.CellGraph.RemoveLandblock(landblockId);
    }

    internal void Clear()
    {
        if (_landblocks.Count != 0)
        {
            var landblocks = new uint[_landblocks.Count];
            _landblocks.Keys.CopyTo(landblocks, 0);
            foreach (uint landblockId in landblocks)
                RemoveLandblock(landblockId);
        }

        ShadowObjects.Clear();
    }

    internal void DemoteLandblockToTerrain(uint landblockId)
    {
        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        if (_landblocks.TryGetValue(canonical, out var landblock))
        {
            _landblocks[canonical] = landblock with
            {
                Cells = Array.Empty<CellSurface>(),
                Portals = Array.Empty<PortalPlane>(),
            };
        }

        ShadowObjects.DeregisterStaticOwnersForLandblock(canonical);
        ShadowObjects.RemoveLandblock(canonical);
        DataCache?.RemoveCellsForLandblock(canonical);
        DataCache?.RemoveBuildingsForLandblock(canonical);
        DataCache?.CellGraph.RemoveEnvCellsForLandblock(canonical);

        if (DataCache?.CellGraph is { } graph
            && graph.CurrCell is { } current
            && (current.Id & 0xFFFF0000u) == (canonical & 0xFFFF0000u)
            && (current.Id & 0xFFFFu) >= 0x0100u)
        {
            graph.CurrCell = null;
        }
    }

    public bool TryGetLandblockContext(float worldX, float worldY,
        out uint landblockId, out float worldOffsetX, out float worldOffsetY)
    {
        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldX - lb.WorldOffsetX;
            float localY = worldY - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
            {
                landblockId = kvp.Key;
                worldOffsetX = lb.WorldOffsetX;
                worldOffsetY = lb.WorldOffsetY;
                return true;
            }
        }
        landblockId = 0;
        worldOffsetX = 0f;
        worldOffsetY = 0f;
        return false;
    }

    public float? SampleTerrainZ(float worldX, float worldY)
    {
        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldX - lb.WorldOffsetX;
            float localY = worldY - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
                return lb.Terrain.SampleZ(localX, localY);
        }
        return null;
    }

    public float SampleWaterDepth(float worldX, float worldY)
    {
        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldX - lb.WorldOffsetX;
            float localY = worldY - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
                return lb.Terrain.SampleWaterDepth(localX, localY);
        }
        return 0f;
    }

    public System.Numerics.Plane? SampleTerrainPlane(float worldX, float worldY)
    {
        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldX - lb.WorldOffsetX;
            float localY = worldY - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
            {
                var (z, normal) = lb.Terrain.SampleSurface(localX, localY);
                float d = -(normal.X * worldX + normal.Y * worldY + normal.Z * z);
                return new System.Numerics.Plane(normal, d);
            }
        }
        return null;
    }


    internal TerrainWalkableSample? SampleTerrainWalkable(float worldX, float worldY)
    {
        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldX - lb.WorldOffsetX;
            float localY = worldY - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
                return BuildTerrainWalkableSample(kvp.Key, lb, localX, localY);
        }
        return null;
    }

    internal TerrainWalkableSample? SampleTerrainWalkableInCell(
        uint cellId,
        float worldX,
        float worldY)
    {
        uint lowCellId = cellId & 0xFFFFu;
        if (lowCellId is < 1u or > 0x40u)
            return null;

        foreach (var kvp in _landblocks)
        {
            uint requestedPrefix = cellId & 0xFFFF0000u;
            if (requestedPrefix != 0u &&
                (kvp.Key & 0xFFFF0000u) != requestedPrefix)
                continue;

            LandblockPhysics lb = kvp.Value;
            float localX = worldX - lb.WorldOffsetX;
            float localY = worldY - lb.WorldOffsetY;
            if (requestedPrefix == 0u &&
                (localX < 0f || localX >= 192f ||
                 localY < 0f || localY >= 192f))
            {
                continue;
            }
            int cellIndex = (int)lowCellId - 1;
            int cellX = cellIndex / TerrainSurface.CellsPerSide;
            int cellY = cellIndex % TerrainSurface.CellsPerSide;
            float minX = cellX * TerrainSurface.CellSize;
            float minY = cellY * TerrainSurface.CellSize;
            float maxX = minX + TerrainSurface.CellSize;
            float maxY = minY + TerrainSurface.CellSize;

            if (localX < minX || localX >= maxX ||
                localY < minY || localY >= maxY)
            {
                return null;
            }

            return BuildTerrainWalkableSample(
                kvp.Key,
                lb,
                localX,
                localY);
        }

        return null;
    }

    private static TerrainWalkableSample BuildTerrainWalkableSample(
        uint landblockId,
        LandblockPhysics landblock,
        float localX,
        float localY)
    {
        TerrainSurfacePolygon sample = landblock.Terrain.SampleSurfacePolygon(
            localX,
            localY);
        var vertices = new TerrainTriangleVertices(
            OffsetTerrainVertex(sample.Vertices.V0, landblock),
            OffsetTerrainVertex(sample.Vertices.V1, landblock),
            OffsetTerrainVertex(sample.Vertices.V2, landblock));

        Vector3 normal = sample.Normal;
        float d = -Vector3.Dot(normal, vertices[0]);
        var plane = new System.Numerics.Plane(normal, d);

        float waterDepth = landblock.Terrain.SampleWaterDepth(localX, localY);
        bool isWater = waterDepth >= 0.45f;
        uint lowCellId = landblock.Terrain.ComputeOutdoorCellId(localX, localY);
        uint fullCellId = (landblockId & 0xFFFF0000u) | lowCellId;

        return new TerrainWalkableSample(
            plane,
            vertices,
            waterDepth,
            isWater,
            fullCellId);
    }

    private static Vector3 OffsetTerrainVertex(Vector3 vertex, LandblockPhysics landblock)
        => new(
            vertex.X + landblock.WorldOffsetX,
            vertex.Y + landblock.WorldOffsetY,
            vertex.Z);

    public void UpdatePlayerCurrCell(uint cellId)
    {
        if (DataCache?.CellGraph is { } cg && cg.GetVisible(cellId) is { } cell)
            cg.CurrCell = cell;
    }

    internal uint ResolveCellId(Vector3 worldPos, float sphereRadius, uint fallbackCellId)
    {
        if (fallbackCellId == 0) return 0;

        // Indoor fallback ids pass through unchanged — identical to the old
        // dead path's `DataCache is null → return fallbackCellId` outcome.
        if ((fallbackCellId & 0xFFFFu) >= 0x0100u) return fallbackCellId;

        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldPos.X - lb.WorldOffsetX;
            float localY = worldPos.Y - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
            {
                uint lowCellId = lb.Terrain.ComputeOutdoorCellId(localX, localY);
                return (kvp.Key & 0xFFFF0000u) | lowCellId;
            }
        }

        return fallbackCellId;
    }

    private readonly record struct AdjustedSetPosition(
        uint CellId,
        Vector3 CellLocalPosition,
        bool Resident);

    private AdjustedSetPosition AdjustSetPosition(
        uint seedCellId,
        Vector3 cellLocalPosition,
        Vector3 firstWorldSphereCenter,
        CellArray queryFootprint)
    {
        queryFootprint.Add(seedCellId);
        uint low = seedCellId & 0xFFFFu;
        bool lowInRange = low is (>= 1u and <= 0x40u)
            or (>= 0x0100u and <= 0xFFFDu)
            or 0xFFFFu;
        if (!lowInRange)
            return new AdjustedSetPosition(
                seedCellId,
                cellLocalPosition,
                Resident: false);

        uint adjustedCell = seedCellId;
        Vector3 adjustedLocal = cellLocalPosition;
        if (low >= 0x0100u)
        {
            PhysicsDataCache? cache = DataCache;
            if (cache is null || cache.GetCellStruct(seedCellId) is null)
            {
                return new AdjustedSetPosition(
                    seedCellId,
                    cellLocalPosition,
                    Resident: false);
            }

            uint child = CellTransit.FindVisibleChildCell(
                cache,
                seedCellId,
                firstWorldSphereCenter,
                useStabList: true,
                queryFootprint);
            if (child != 0u)
            {
                return new AdjustedSetPosition(
                    child,
                    adjustedLocal,
                    Resident: cache.GetCellStruct(child) is not null);
            }

            CellPhysics? claimed = cache.GetCellStruct(seedCellId);
            if (claimed is null || !claimed.SeenOutside)
            {
                return new AdjustedSetPosition(
                    seedCellId,
                    adjustedLocal,
                    Resident: false);
            }
        }

        bool adjusted = LandDefs.AdjustToOutside(
            ref adjustedCell,
            ref adjustedLocal);
        queryFootprint.Add(adjustedCell);
        bool resident = adjusted
            && IsLandblockTerrainResident(adjustedCell);
        return new AdjustedSetPosition(
            adjustedCell,
            adjustedLocal,
            resident);
    }

    internal PhysicsSetPositionResult SetPosition(
        in PhysicsSetPositionRequest request,
        Func<PhysicsSetPositionCollisionReport, bool>? handleCollisions = null)
    {
        if (_transitionScratch?.ActiveDepth >= TransitionScratchArena.Capacity)
        {
            return ErrorResult(
                request,
                PhysicsSetPositionError.GeneralFailure);
        }

        Transition transition = RentTransition();
        CellArray queryFootprint =
            transition.SpherePath.SetPositionQueryFootprint;
        try
        {
            queryFootprint.Clear();
            transition.SpherePath.CellCandidates.UnionTarget = queryFootprint;
            InitializeSetPositionTransition(transition, request);
            bool randomOnly = request.Flags.HasFlag(
                PhysicsSetPositionFlags.RandomScatter);
            PhysicsSetPositionResult result;
            if (randomOnly)
            {
                result = SetScatterPositionInternal(
                    transition,
                    request,
                    handleCollisions,
                    queryFootprint);
            }
            else
            {
                result = SetPositionInternal(
                    transition,
                    request,
                    handleCollisions,
                    queryFootprint);
                if (result.Error != PhysicsSetPositionError.Ok
                    && request.Flags.HasFlag(PhysicsSetPositionFlags.Scatter))
                {
                    result = SetScatterPositionInternal(
                        transition,
                        request,
                        handleCollisions,
                        queryFootprint);
                }
            }

            return result with
            {
                QueriedCellIds = queryFootprint.OrderedIds.ToImmutableArray(),
            };
        }
        finally
        {
            transition.SpherePath.CellCandidates.UnionTarget = null;
            ReturnTransition(transition);
        }
    }

    private void InitializeSetPositionTransition(
        Transition transition,
        in PhysicsSetPositionRequest request)
    {
        transition.ObjectInfo.StepUpHeight = request.StepUpHeight;
        transition.ObjectInfo.StepDownHeight = request.StepDownHeight;
        transition.ObjectInfo.StepDown =
            !request.MoverPhysicsState.HasFlag(PhysicsStateFlags.Missile);
        transition.ObjectInfo.MoverPhysicsState = request.MoverPhysicsState;
        transition.ObjectInfo.SelfEntityId = request.MovingEntityId;
        transition.ObjectInfo.State = request.MoverFlags;
        transition.ObjectInfo.Ethereal = request.MoverPhysicsState.HasFlag(
            PhysicsStateFlags.Ethereal);
        transition.SpherePath.PlacementAllowsSliding =
            request.Flags.HasFlag(PhysicsSetPositionFlags.Slide);
    }

    private PhysicsSetPositionResult SetScatterPositionInternal(
        Transition transition,
        in PhysicsSetPositionRequest request,
        Func<PhysicsSetPositionCollisionReport, bool>? handleCollisions,
        CellArray queryFootprint)
    {
        PhysicsSetPositionResult result = ErrorResult(
            request,
            PhysicsSetPositionError.GeneralFailure);
        for (uint attempt = 0u; attempt < request.ScatterAttempts; attempt++)
        {
            float dx = ((float)((SetPositionRandomUnit() * 2d) - 1d))
                * request.ScatterRadiusX;
            float dy = ((float)((SetPositionRandomUnit() * 2d) - 1d))
                * request.ScatterRadiusY;
            var scattered = request with
            {
                Position = request.Position + new Vector3(dx, dy, 0f),
                CellLocalPosition = request.CellLocalPosition
                    + new Vector3(dx, dy, 0f),
            };
            result = SetPositionInternal(
                transition,
                scattered,
                handleCollisions,
                queryFootprint);
            if (result.Error == PhysicsSetPositionError.Ok)
                break;
        }
        return result;
    }

    private PhysicsSetPositionResult SetPositionInternal(
        Transition transition,
        in PhysicsSetPositionRequest request,
        Func<PhysicsSetPositionCollisionReport, bool>? handleCollisions,
        CellArray queryFootprint)
    {
        transition.SpherePath.CellCandidates.Clear();
        transition.SpherePath.ClearWalkable();
        ImmutableArray<FlatCollisionSphere> spheres = request.Spheres;
        float sphereScale = spheres.IsDefaultOrEmpty ? 1f : request.Scale;
        Vector3 firstLocalCenter = spheres.IsDefaultOrEmpty
            ? new Vector3(0f, 0f, PhysicsGlobals.DummySphereRadius)
            : spheres[0].Origin * sphereScale;
        Vector3 firstWorldCenter =
            Vector3.Transform(firstLocalCenter, request.Orientation)
            + request.Position;
        AdjustedSetPosition adjusted = AdjustSetPosition(
            request.CellId,
            request.CellLocalPosition,
            firstWorldCenter,
            queryFootprint);
        if (!adjusted.Resident)
        {
            return new PhysicsSetPositionResult(
                PhysicsSetPositionError.Ok,
                PhysicsResidenceDisposition.DeferredCell,
                request.Position,
                request.Orientation,
                adjusted.CellId,
                adjusted.CellLocalPosition,
                CrossCellIds: ImmutableArray<uint>.Empty,
                CollidedObjectIds: ImmutableArray<uint>.Empty);
        }

        bool forceIntoCell = request.PlacementClass is
            PhysicsPlacementClass.Hook
            or PhysicsPlacementClass.Storage
            or PhysicsPlacementClass.Corpse;
        if (forceIntoCell)
        {
            if (adjusted.CellId == 0u)
            {
                return ErrorResult(
                    request,
                    PhysicsSetPositionError.NoCell);
            }
            bool changedCell = request.CurrentCellId is null
                || request.CurrentCellId.Value != adjusted.CellId;
            return new PhysicsSetPositionResult(
                PhysicsSetPositionError.Ok,
                PhysicsResidenceDisposition.Committed,
                request.Position,
                request.Orientation,
                adjusted.CellId,
                adjusted.CellLocalPosition,
                CellChanged: changedCell,
                ShadowAction: changedCell
                    ? PhysicsShadowCommitAction.Recalculate
                    : PhysicsShadowCommitAction.None,
                CrossCellIds: ImmutableArray<uint>.Empty,
                CollidedObjectIds: ImmutableArray<uint>.Empty);
        }

        transition.SpherePath.InitPath(
            request.Position,
            request.Position,
            adjusted.CellId,
            spheres,
            sphereScale,
            request.Orientation,
            request.Orientation);
        transition.SpherePath.InsertType = InsertType.Placement;
        transition.SpherePath.PlacementAllowsSliding =
            request.Flags.HasFlag(PhysicsSetPositionFlags.Slide);

        bool valid = transition.FindValidPosition(this);
        SpherePath spherePath = transition.SpherePath;
        if (valid
            && !request.Flags.HasFlag(PhysicsSetPositionFlags.Slide))
        {
            valid = AcceptNoSlidePlacement(
                spherePath.CurPos,
                request.Position,
                spherePath.CurCellId,
                adjusted.CellId);
        }

        CollisionInfo collision = transition.CollisionInfo;
        var collisionReport = new PhysicsSetPositionCollisionReport(
            collision.ContactPlaneValid,
            collision.ContactPlane,
            collision.ContactPlaneCellId,
            collision.ContactPlaneIsWater,
            collision.LastKnownContactPlaneValid,
            collision.LastKnownContactPlane,
            collision.LastKnownContactPlaneCellId,
            collision.LastKnownContactPlaneIsWater,
            collision.SlidingNormalValid,
            collision.SlidingNormal,
            collision.CollisionNormalValid,
            collision.CollisionNormal,
            collision.CollidedWithEnvironment,
            collision.FramesStationaryFall,
            collision.AdjustOffset,
            collision.LastCollidedObjectGuid,
            collision.CollideObjectGuids.ToImmutableArray());
        bool collisionHandlerResult = !valid
            && handleCollisions?.Invoke(collisionReport) == true;
        if (!valid)
        {
            return new PhysicsSetPositionResult(
                collisionHandlerResult
                    ? PhysicsSetPositionError.Collided
                    : PhysicsSetPositionError.NoValidPosition,
                PhysicsResidenceDisposition.Unchanged,
                request.Position,
                request.Orientation,
                request.CellId,
                request.CellLocalPosition,
                InContact: collision.ContactPlaneValid,
                OnWalkable: PhysicsObjUpdate.IsWalkableContact(
                    collision.ContactPlaneValid,
                    collision.ContactPlane.Normal),
                ContactPlane: collision.ContactPlane,
                ContactPlaneCellId: collision.ContactPlaneCellId,
                ContactPlaneIsWater: collision.ContactPlaneIsWater,
                SlidingNormalValid: collision.SlidingNormalValid,
                SlidingNormal: collision.SlidingNormal,
                CollisionNormalValid: collision.CollisionNormalValid,
                CollisionNormal: collision.CollisionNormal,
                FramesStationaryFall: collision.FramesStationaryFall,
                CollisionHandlerResult: collisionHandlerResult,
                CollidedWithEnvironment: collision.CollidedWithEnvironment,
                CrossCellIds: ImmutableArray<uint>.Empty,
                CollidedObjectIds: collisionReport.CollidedObjectIds);
        }
        if (spherePath.CurCellId == 0u)
        {
            return ErrorResult(
                request,
                PhysicsSetPositionError.NoCell);
        }

        bool inContact = collision.ContactPlaneValid;
        bool onWalkable = PhysicsObjUpdate.IsWalkableContact(
            inContact,
            collision.ContactPlane.Normal);
        Vector3 resultLocal = adjusted.CellLocalPosition
            + (spherePath.CurPos - request.Position)
            - LandDefs.GetBlockOffset(adjusted.CellId, spherePath.CurCellId);
        bool hasPhysicsBsp = request.MoverPhysicsState.HasFlag(
            PhysicsStateFlags.HasPhysicsBsp);
        ImmutableArray<uint> transitionCells =
            spherePath.CellCandidates.OrderedIds.ToImmutableArray();
        PhysicsShadowCommitAction shadowAction = hasPhysicsBsp
            ? PhysicsShadowCommitAction.Recalculate
            : transitionCells.Length != 0
                ? PhysicsShadowCommitAction.Replace
                : PhysicsShadowCommitAction.Preserve;
        return new PhysicsSetPositionResult(
            PhysicsSetPositionError.Ok,
            PhysicsResidenceDisposition.Committed,
            spherePath.CurPos,
            request.Orientation,
            spherePath.CurCellId,
            resultLocal,
            inContact,
            onWalkable,
            collision.ContactPlane,
            collision.ContactPlaneCellId,
            collision.ContactPlaneIsWater,
            collision.SlidingNormalValid,
            collision.SlidingNormal,
            collision.CollisionNormalValid,
            collision.CollisionNormal,
            collision.FramesStationaryFall,
            collision.CollidedWithEnvironment,
            collisionHandlerResult,
            CellChanged: request.CurrentCellId is null
                || request.CurrentCellId.Value != spherePath.CurCellId,
            ShadowAction: shadowAction,
            CrossCellIds: shadowAction == PhysicsShadowCommitAction.Replace
                ? transitionCells
                : ImmutableArray<uint>.Empty,
            CollidedObjectIds:
                collision.CollideObjectGuids.ToImmutableArray());
    }

    private static PhysicsSetPositionResult ErrorResult(
        in PhysicsSetPositionRequest request,
        PhysicsSetPositionError error) => new(
            error,
            PhysicsResidenceDisposition.Unchanged,
            request.Position,
            request.Orientation,
            request.CellId,
            request.CellLocalPosition,
            CrossCellIds: ImmutableArray<uint>.Empty,
            CollidedObjectIds: ImmutableArray<uint>.Empty);

    internal static bool AcceptNoSlidePlacement(
        Vector3 resolvedPosition,
        Vector3 requestedPosition,
        uint resolvedCellId,
        uint adjustedCellId)
    {
        Vector3 displacement = resolvedPosition - requestedPosition;
        return displacement.X <= 0.0500000007f
            && displacement.Y <= 0.0500000007f
            && resolvedCellId == adjustedCellId;
    }

    private float? WalkableFloorZNearest(uint cellId, Vector3 worldPos, float referenceZ)
    {
        var cp = DataCache?.GetCellStruct(cellId);
        if (cp is null) return null;

        var local = Vector3.Transform(
            new Vector3(worldPos.X, worldPos.Y, referenceZ), cp.InverseWorldTransform);

        float? best = null;
        float bestDist = float.MaxValue;
        FlatPhysicsBsp? flat = cp.FlatPhysicsBsp;
        if (flat is not null)
        {
            FlatPolygonTable table = flat.PolygonTable;
            for (int i = 0; i < table.Polygons.Length; i++)
            {
                FlatCollisionPolygon poly = table.Polygons[i];
                Vector3 n = poly.Plane.Normal;
                if (n.Z < PhysicsGlobals.FloorZ) continue;
                if (!PointInPolygonXY(table, poly.VertexRange, local.X, local.Y))
                    continue;
                float lz =
                    -(n.X * local.X + n.Y * local.Y + poly.Plane.D) / n.Z;
                float wz = Vector3.Transform(
                    new Vector3(local.X, local.Y, lz),
                    cp.WorldTransform).Z;
                float dist = MathF.Abs(wz - referenceZ);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = wz;
                }
            }
            return best;
        }

        if (DataCache!.CollisionTraversalMode == CollisionTraversalMode.Flat)
        {
            throw new InvalidOperationException(
                $"Production CellStruct 0x{cellId:X8} has no prepared physics BSP.");
        }

        foreach (var kv in cp.Resolved)
        {
            var poly = kv.Value;
            var n = poly.Plane.Normal;
            if (n.Z < PhysicsGlobals.FloorZ) continue;
            if (!PointInPolygonXY(poly.Vertices, local.X, local.Y)) continue;
            // plane: n·p + d = 0  =>  z = -(n.x*x + n.y*y + d)/n.z
            float lz = -(n.X * local.X + n.Y * local.Y + poly.Plane.D) / n.Z;
            float wz = Vector3.Transform(new Vector3(local.X, local.Y, lz), cp.WorldTransform).Z;
            float dist = MathF.Abs(wz - referenceZ);
            if (dist < bestDist) { bestDist = dist; best = wz; }
        }
        return best;
    }

    private static bool PointInPolygonXY(
        FlatPolygonTable table,
        FlatIndexRange range,
        float x,
        float y)
    {
        bool inside = false;
        int end = range.EndExclusive;
        for (int i = range.Start, j = end - 1; i < end; j = i++)
        {
            Vector3 vi = table.Vertices[i];
            Vector3 vj = table.Vertices[j];
            if ((vi.Y > y) != (vj.Y > y)
                && x < (vj.X - vi.X) * (y - vi.Y) /
                    (vj.Y - vi.Y) + vi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static bool PointInPolygonXY(IReadOnlyList<Vector3> verts, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = verts.Count - 1; i < verts.Count; j = i++)
        {
            var vi = verts[i]; var vj = verts[j];
            if ((vi.Y > y) != (vj.Y > y)
                && x < (vj.X - vi.X) * (y - vi.Y) / (vj.Y - vi.Y) + vi.X)
                inside = !inside;
        }
        return inside;
    }

    public bool IsSpawnCellReady(uint cellId)
    {
        if ((cellId & 0xFFFFu) < 0x0100u) return true;
        return DataCache?.GetCellStruct(cellId) is not null;
    }

    public (uint cellId, bool found) AdjustPosition(uint seedCellId, Vector3 worldPoint)
    {
        if (seedCellId == 0u) return (seedCellId, false);

        if ((seedCellId & 0xFFFFu) >= 0x0100u)
        {
            // Indoor: find_visible_child_cell(this, point, arg3 = 1) (:280028).
            if (DataCache is null) return (seedCellId, false);
            uint child = CellTransit.FindVisibleChildCell(DataCache, seedCellId, worldPoint, useStabList: true);
            if (child != 0u) return (child, true);

            var claimed = DataCache.GetCellStruct(seedCellId);
            if (claimed is null || !claimed.SeenOutside)
                return (seedCellId, false);
        }

        foreach (var kvp in _landblocks)
        {
            var lb = kvp.Value;
            float localX = worldPoint.X - lb.WorldOffsetX;
            float localY = worldPoint.Y - lb.WorldOffsetY;
            if (localX >= 0f && localX < 192f && localY >= 0f && localY < 192f)
            {
                uint lowCellId = lb.Terrain.ComputeOutdoorCellId(localX, localY);
                return ((kvp.Key & 0xFFFF0000u) | lowCellId, true);
            }
        }

        return (seedCellId, false);
    }

    public ResolveResult ResolveWithTransition(
        Vector3 currentPos, Vector3 targetPos, uint cellId,
        float sphereRadius, float sphereHeight,
        float stepUpHeight, float stepDownHeight,
        bool isOnGround,
        PhysicsBody? body = null,
        ObjectInfoState moverFlags = ObjectInfoState.None,
        uint movingEntityId = 0,
        Vector3? localSphereOrigin = null,
        Quaternion? beginOrientation = null,
        Quaternion? endOrientation = null,
        uint designatedTargetId = 0,
        ImmutableArray<FlatCollisionSphere> sphereList = default,
        float sphereScale = 1f)
    {
        bool captureEnabled = PhysicsResolveCapture.IsEnabled
                              && (moverFlags & ObjectInfoState.IsPlayer) != 0;
        PhysicsBodySnapshot? bodyBeforeSnap =
            captureEnabled && body is not null
                ? PhysicsResolveCapture.Snapshot(body)
                : null;

        PhysicsDiagnostics.BeginTransitFailTrace();

        var transition = RentTransition();
        try
        {
            transition.ObjectInfo.StepUpHeight = stepUpHeight;
            transition.ObjectInfo.StepDownHeight = stepDownHeight;

            transition.ObjectInfo.StepDown = true;
            transition.ObjectInfo.SelfEntityId = movingEntityId;
            transition.ObjectInfo.MoverPhysicsState = body?.State ?? PhysicsStateFlags.None;
            transition.ObjectInfo.TargetId = designatedTargetId;

            transition.ObjectInfo.State |= moverFlags;

            if ((transition.ObjectInfo.MoverPhysicsState & PhysicsStateFlags.Missile) != 0)
                transition.ObjectInfo.State |= ObjectInfoState.PathClipped;

            transition.ObjectInfo.MoverHasGravity = body?.HasGravity ?? false;

            if (body is not null && body.InContact && body.ContactPlaneValid)
            {
                float awayRate = Vector3.Dot(body.Velocity, body.ContactPlane.Normal);
                if (awayRate <= PhysicsGlobals.EPSILON)
                {
                    transition.ObjectInfo.State |= ObjectInfoState.Contact;
                    if (body.OnWalkable)
                        transition.ObjectInfo.State |= ObjectInfoState.OnWalkable;
                    transition.CollisionInfo.InitContactPlane(
                        body.ContactPlane,
                        body.ContactPlaneCellId,
                        body.ContactPlaneIsWater);
                }
                else
                {
                    transition.CollisionInfo.LastKnownContactPlaneValid = true;
                    transition.CollisionInfo.LastKnownContactPlane = body.ContactPlane;
                    transition.CollisionInfo.LastKnownContactPlaneCellId = body.ContactPlaneCellId;
                    transition.CollisionInfo.LastKnownContactPlaneIsWater = body.ContactPlaneIsWater;
                }
            }
            else if (body is null && isOnGround)
            {
                transition.ObjectInfo.State |= ObjectInfoState.Contact | ObjectInfoState.OnWalkable;
            }

            if (body is not null
                && (body.TransientState & TransientStateFlags.Sliding) != 0
                && body.SlidingNormal.LengthSquared() > PhysicsGlobals.EpsilonSq)
            {
                transition.CollisionInfo.SetSlidingNormal(body.SlidingNormal);
            }

            if (!sphereList.IsDefaultOrEmpty)
            {
                transition.SpherePath.InitPath(
                    currentPos,
                    targetPos,
                    cellId,
                    sphereList,
                    sphereScale,
                    beginOrientation,
                    endOrientation);
            }
            else
            {
                transition.SpherePath.InitPath(
                    currentPos,
                    targetPos,
                    cellId,
                    sphereRadius,
                    sphereHeight,
                    localSphereOrigin,
                    beginOrientation,
                    endOrientation);
            }

            transition.SpherePath.CarriedBlockOrigin =
                body is not null
                && (cellId & 0xFFFFu) is >= 1u and <= 0x40u
                && (body.CellPosition.ObjCellId & 0xFFFFu) is >= 1u and <= 0x40u
                && (cellId >> 16) == (body.CellPosition.ObjCellId >> 16)
                    ? body.Position - body.CellPosition.Frame.Origin
                    : null;

            if (isOnGround && body is not null
                && body.WalkablePolygonValid
                && body.WalkableVertices is { Length: >= 3 })
            {
                transition.SpherePath.SetWalkable(
                    body.WalkablePlane,
                    body.WalkableVertices,
                    body.WalkableUp);
            }

            if (body is not null)
            {
                transition.CollisionInfo.FramesStationaryFall =
                    (body.TransientState & TransientStateFlags.StationaryStuck) != 0 ? 3 :
                    (body.TransientState & TransientStateFlags.StationaryStop) != 0 ? 2 :
                    (body.TransientState & TransientStateFlags.StationaryFall) != 0 ? 1 : 0;
            }

            bool ok = transition.FindTransitionalPosition(this);

            var sp = transition.SpherePath;
            var ci = transition.CollisionInfo;

            if (body is not null)
            {
                if (ok)
                {
                    if (ci.ContactPlaneValid)
                    {
                        body.ContactPlaneValid = true;
                        body.ContactPlane = ci.ContactPlane;
                        body.ContactPlaneCellId = ci.ContactPlaneCellId;
                        body.ContactPlaneIsWater = ci.ContactPlaneIsWater;
                        body.GroundNormal = ci.ContactPlane.Normal;
                    }
                    else if (ci.LastKnownContactPlaneValid)
                    {
                        body.ContactPlaneValid = true;
                        body.ContactPlane = ci.LastKnownContactPlane;
                        body.ContactPlaneCellId = ci.LastKnownContactPlaneCellId;
                        body.ContactPlaneIsWater = ci.LastKnownContactPlaneIsWater;
                        body.GroundNormal = ci.LastKnownContactPlane.Normal;
                    }
                    else
                    {
                        body.ContactPlaneValid = false;
                    }

                    if (body.ContactPlaneIsWater)
                        body.TransientState |= TransientStateFlags.WaterContact;
                    else
                        body.TransientState &= ~TransientStateFlags.WaterContact;

                    body.FramesStationaryFall = ci.FramesStationaryFall;
                    body.TransientState &= ~(TransientStateFlags.StationaryFall
                                           | TransientStateFlags.StationaryStop
                                           | TransientStateFlags.StationaryStuck);
                    body.TransientState |= ci.FramesStationaryFall switch
                    {
                        1 => TransientStateFlags.StationaryFall,
                        2 => TransientStateFlags.StationaryStop,
                        3 => TransientStateFlags.StationaryStuck,
                        _ => TransientStateFlags.None,
                    };

                    if (sp.HasLastWalkablePolygon && sp.LastWalkableVertices is not null)
                    {
                        body.WalkablePolygonValid = true;
                        body.WalkablePlane = sp.LastWalkablePlane;
                        body.SetWalkableVerticesExact(sp.LastWalkableVertices);
                        body.WalkableUp = sp.LastWalkableUp;
                    }
                    else if (!isOnGround && !ci.ContactPlaneValid && !ci.LastKnownContactPlaneValid)
                    {
                        body.WalkablePolygonValid = false;
                        body.WalkableVertices = null;
                    }

                    if (ci.SlidingNormalValid
                        && ci.SlidingNormal.LengthSquared() > PhysicsGlobals.EpsilonSq)
                    {
                        body.SlidingNormal = ci.SlidingNormal;
                        body.TransientState |= TransientStateFlags.Sliding;
                    }
                    else
                    {
                        body.SlidingNormal = Vector3.Zero;
                        body.TransientState &= ~TransientStateFlags.Sliding;
                    }
                }

                if (transition.ObjectInfo.VelocityKilled)
                {
                    if (PhysicsDiagnostics.DumpSteepRoofEnabled)
                        Console.WriteLine($"[steep-roof] KILL-VELOCITY-APPLIED Vbefore=({body.Velocity.X:F2},{body.Velocity.Y:F2},{body.Velocity.Z:F2}) → 0,0,0");
                    body.Velocity = Vector3.Zero;
                }
            }

            bool collisionNormalValid = ci.CollisionNormalValid;
            Vector3 collisionNormal = ci.CollisionNormal;

            if (PhysicsDiagnostics.ProbeResolveEnabled)
            {
                var probePost = sp.CheckPos;
                string probeCp = ci.ContactPlaneValid
                    ? "valid"
                    : (ci.LastKnownContactPlaneValid ? "lastKnown" : "none");
                string probeHit;
                if (collisionNormalValid)
                {
                    string objPart = ci.LastCollidedObjectGuid.HasValue
                        ? System.FormattableString.Invariant(
                            $" obj=0x{ci.LastCollidedObjectGuid.Value:X8}")
                        : "";
                    string envPart = ci.CollidedWithEnvironment ? " env" : "";
                    int objCount = ci.CollideObjectGuids.Count;
                    string objCountPart = objCount > 1
                        ? System.FormattableString.Invariant($" nObj={objCount}")
                        : "";
                    probeHit = System.FormattableString.Invariant(
                        $"yes n=({collisionNormal.X:F2},{collisionNormal.Y:F2},{collisionNormal.Z:F2}){objPart}{envPart}{objCountPart}");
                }
                else
                {
                    probeHit = "no";
                }
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[resolve] ent=0x{movingEntityId:X8} in=({currentPos.X:F3},{currentPos.Y:F3},{currentPos.Z:F3}) cell=0x{cellId:X8} tgt=({targetPos.X:F3},{targetPos.Y:F3},{targetPos.Z:F3}) out=({probePost.X:F3},{probePost.Y:F3},{probePost.Z:F3}) cell=0x{sp.CheckCellId:X8} ok={ok} groundedIn={isOnGround} cp={probeCp} hit={probeHit} walkable={sp.HasLastWalkablePolygon}"));
            }

            if (PhysicsDiagnostics.ProbeSweptEnabled)
            {
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[cell-swept] ent=0x{movingEntityId:X8} ok={ok} inCell=0x{cellId:X8} curCell=0x{sp.CurCellId:X8} checkCell=0x{sp.CheckCellId:X8} curPos=({sp.CurPos.X:F3},{sp.CurPos.Y:F3},{sp.CurPos.Z:F3}) checkPos=({sp.CheckPos.X:F3},{sp.CheckPos.Y:F3},{sp.CheckPos.Z:F3})"));
            }

            ResolveResult resolveResult;
            if (ok)
            {
                bool inContact = ci.ContactPlaneValid;
                bool onWalkable = PhysicsObjUpdate.IsWalkableContact(
                    inContact,
                    ci.ContactPlane.Normal);
                bool onGround = inContact
                    || (transition.ObjectInfo.State & ObjectInfoState.OnWalkable) != 0;

                resolveResult = new ResolveResult(
                    sp.CheckPos,
                    sp.CurCellId,
                    onGround,
                    collisionNormalValid,
                    collisionNormal,
                    Orientation: sp.CurOrientation,
                    InContact: inContact,
                    OnWalkable: onWalkable);
            }
            else
            {
                bool partialOnGround = ci.ContactPlaneValid
                    || (transition.ObjectInfo.State & ObjectInfoState.OnWalkable) != 0
                    || isOnGround;

                uint partialCellId = sp.CheckCellId != 0 ? sp.CheckCellId : cellId;
                resolveResult = new ResolveResult(
                    sp.CheckPos,
                    sp.CurCellId != 0 ? sp.CurCellId : partialCellId,
                    partialOnGround,
                    collisionNormalValid,
                    collisionNormal,
                    Ok: false,
                    Orientation: sp.CurOrientation);   // Render Residual A — the sweep failed (find_valid_position == 0)
            }

            PhysicsDiagnostics.EmitTransitFailIfStuck(
                movingEntityId, currentPos, targetPos, resolveResult.Position);

            if (captureEnabled)
            {
                PhysicsResolveCapture.LogCall(
                    new ResolveCallInputs(
                        CurrentPos: currentPos,
                        TargetPos: targetPos,
                        CellId: cellId,
                        SphereRadius: sphereRadius,
                        SphereHeight: sphereHeight,
                        StepUpHeight: stepUpHeight,
                        StepDownHeight: stepDownHeight,
                        IsOnGround: isOnGround,
                        MoverFlags: (uint)moverFlags,
                        MovingEntityId: movingEntityId),
                    bodyBeforeSnap,
                    new ResolveCallResult(
                        Position: resolveResult.Position,
                        CellId: resolveResult.CellId,
                        IsOnGround: resolveResult.IsOnGround,
                        CollisionNormalValid: resolveResult.CollisionNormalValid,
                        CollisionNormal: resolveResult.CollisionNormal),
                    body is not null ? PhysicsResolveCapture.Snapshot(body) : null);
            }

            return resolveResult with
            {
                LastCollidedObjectId = ci.LastCollidedObjectGuid ?? 0u,
                CollidedWithEnvironment = ci.CollidedWithEnvironment,
            };
        }
        finally
        {
            ReturnTransition(transition);
        }
    }
}
