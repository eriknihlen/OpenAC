using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;   // TerrainSurface

namespace AcDream.Core.World.Cells;

public sealed class CellGraph
{
    private readonly CollisionWorldStateSlot _collisionWorld;
    private ConcurrentDictionary<uint, EnvCell> _envCells =>
        _collisionWorld.Current.EnvCells;
    private ConcurrentDictionary<uint, CellGraphTerrain> _terrain =>
        _collisionWorld.Current.Terrain;
    private ConcurrentDictionary<uint, ObjCell> _outdoorCells =>
        _collisionWorld.Current.OutdoorCells;

    public CellGraph()
        : this(new CollisionWorldStateSlot())
    {
    }

    internal CellGraph(CollisionWorldStateSlot collisionWorld)
    {
        _collisionWorld = collisionWorld
            ?? throw new ArgumentNullException(nameof(collisionWorld));
    }

    public ObjCell? CurrCell { get; internal set; }

    public bool Contains(uint envCellId) => _envCells.ContainsKey(envCellId);

    public void Add(EnvCell cell) =>
        _collisionWorld.Current.TryAddEnvCell(cell.Id, cell);

    public void RegisterTerrain(uint landblockPrefix, TerrainSurface terrain, Vector3 worldOrigin)
    {
        uint prefix = landblockPrefix & 0xFFFF0000u;
        _terrain[prefix] = new CellGraphTerrain(terrain, worldOrigin);
        for (uint low = 1u; low <= 0x40u; low++)
        {
            uint id = prefix | low;
            int index = (int)(low - 1u);
            _outdoorCells[id] = LandCell.Synthesize(
                id,
                terrain,
                worldOrigin,
                index / 8,
                index % 8);
        }
    }

    public bool TryGetTerrainOrigin(uint id, out Vector3 origin)
    {
        if (_terrain.TryGetValue(id & 0xFFFF0000u, out var t))
        {
            origin = t.Origin;
            return true;
        }
        origin = Vector3.Zero;
        return false;
    }

    public void RemoveLandblock(uint landblockPrefix)
    {
        uint lb = landblockPrefix & 0xFFFF0000u;
        if (CurrCell is { } current
            && (current.Id & 0xFFFF0000u) == lb)
        {
            CurrCell = null;
        }
        _terrain.TryRemove(lb, out _);
        for (uint low = 1u; low <= 0x40u; low++)
            _outdoorCells.TryRemove(lb | low, out _);
        RemoveEnvCellPrefixKeys(lb);
    }

    private void RemoveEnvCellPrefixKeys(uint prefix)
    {
        CollisionWorldState world = _collisionWorld.Current;
        List<uint>? slots = world.EnvCellKeys.SlotsForPrefix(prefix);
        if (slots is null)
            return;
        int limit = slots.Count;
        for (int index = 0; index < limit; index++)
        {
            uint id = slots[index];
            if (id != 0u)
                world.RemoveEnvCell(id);
        }
    }

    public void RemoveEnvCellsForLandblock(uint landblockPrefix)
    {
        uint lb = landblockPrefix & 0xFFFF0000u;
        if (CurrCell is { } current
            && (current.Id & 0xFFFF0000u) == lb
            && (current.Id & 0xFFFFu) >= 0x0100u)
        {
            CurrCell = null;
        }
        RemoveEnvCellPrefixKeys(lb);
    }

    public ObjCell? GetVisible(uint id)
    {
        if (id == 0u) return null;
        if ((id & 0xFFFFu) >= 0x100u)
            return _envCells.TryGetValue(id, out var env) ? env : null;

        uint low = id & 0xFFFFu;
        if (low < 1u || low > 0x40u) return null;
        return _outdoorCells.TryGetValue(id, out ObjCell? cell)
            ? cell
            : null;
    }

    public ObjCell? Neighbor(ObjCell cell, in CellPortal portal) => GetVisible(portal.OtherCellId);

    public EnvCell? FindVisibleChildCell(uint rootId, Vector3 worldPoint)
    {
        if (!_envCells.TryGetValue(rootId, out var root)) return null;
        if (root.PointInCell(worldPoint)) return root;
        foreach (var stabId in root.StabList)
            if (_envCells.TryGetValue(stabId, out var stab) && stab.PointInCell(worldPoint))
                return stab;
        return null;
    }

    internal LandblockReplacementBuilder CreateLandblockReplacementBuilder(
        CellGraph staging,
        uint landblockId) => new(this, staging, landblockId);

    internal sealed class LandblockReplacementBuilder : IDisposable
    {
        private readonly CellGraph _active;
        private readonly CellGraph _staging;
        private readonly uint _prefix;
        private readonly List<KeyValuePair<uint, EnvCell>> _envCells = new();
        private readonly HashSet<uint> _stagingIds = new();
        private readonly List<uint> _removeIds = new();
        private List<uint>? _keySlots;
        private int _keySlotLimit;
        private bool _keySlotsCaptured;
        private int _cursor;
        private int _phase;

        internal LandblockReplacementBuilder(
            CellGraph active,
            CellGraph staging,
            uint landblockId)
        {
            _active = active;
            _staging = staging;
            _prefix = landblockId & 0xFFFF0000u;
        }

        internal bool Advance()
        {
            if (_phase == 0)
            {
                // O1: enumerate the staging root's installed target-prefix
                // EnvCell keys via the ledger instead of scanning the map.
                if (TryTakeNextPrefixKey(
                        _staging._collisionWorld.Current.EnvCellKeys,
                        out uint stagingId))
                {
                    if ((stagingId & 0xFFFFu) >= 0x0100u
                        && _staging._envCells.TryGetValue(
                            stagingId,
                            out EnvCell? cell))
                    {
                        _envCells.Add(
                            new KeyValuePair<uint, EnvCell>(stagingId, cell));
                        _stagingIds.Add(stagingId);
                    }
                    return false;
                }
                _phase = 1;
                return false;
            }
            if (_phase == 1)
            {
                if (TryTakeNextPrefixKey(
                        _active._collisionWorld.Current.EnvCellKeys,
                        out uint activeId))
                {
                    if ((activeId & 0xFFFFu) >= 0x0100u
                        && !_stagingIds.Contains(activeId)
                        && _active._envCells.ContainsKey(activeId))
                    {
                        _removeIds.Add(activeId);
                    }
                    return false;
                }
                bool hasTerrain = _staging._terrain.TryGetValue(
                    _prefix,
                    out var terrain);
                Prepared = new PreparedCellGraphLandblock(
                    _prefix,
                    _removeIds,
                    _envCells,
                    hasTerrain,
                    terrain);
                _phase = 2;
            }
            return true;
        }

        private bool TryTakeNextPrefixKey(PrefixKeyIndex ledger, out uint key)
        {
            if (!_keySlotsCaptured)
            {
                _keySlots = ledger.SlotsForPrefix(_prefix);
                _keySlotLimit = _keySlots?.Count ?? 0;
                _keySlotsCaptured = true;
                _cursor = 0;
            }
            while (_cursor < _keySlotLimit)
            {
                uint candidate = _keySlots![_cursor++];
                if (candidate != 0u)
                {
                    key = candidate;
                    return true;
                }
            }
            key = 0u;
            _keySlots = null;
            _keySlotsCaptured = false;
            return false;
        }

        internal PreparedCellGraphLandblock? Prepared { get; private set; }

        public void Dispose()
        {
            _keySlots = null;
            _keySlotsCaptured = false;
        }
    }
}

internal sealed record PreparedCellGraphLandblock(
    uint LandblockPrefix,
    IReadOnlyList<uint> EnvCellIdsToRemove,
    IReadOnlyList<KeyValuePair<uint, EnvCell>> EnvCells,
    bool HasTerrain,
    CellGraphTerrain? Terrain);

internal sealed record CellGraphTerrain(
    TerrainSurface Terrain,
    Vector3 Origin);
