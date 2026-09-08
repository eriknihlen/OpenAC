using System.Collections.Concurrent;
using AcDream.Core.World.Cells;

namespace AcDream.Core.Physics;

internal sealed class PrefixKeyIndex
{
    private readonly Dictionary<uint, List<uint>> _slots = new();
    private readonly Dictionary<uint, Dictionary<uint, int>> _indices = new();
    private readonly Dictionary<uint, Stack<int>> _freeSlots = new();

    internal void Add(uint key)
    {
        uint prefix = key & 0xFFFF0000u;
        if (!_slots.TryGetValue(prefix, out List<uint>? slots))
        {
            slots = new List<uint>();
            _slots[prefix] = slots;
            _indices[prefix] = new Dictionary<uint, int>();
            _freeSlots[prefix] = new Stack<int>();
        }
        Dictionary<uint, int> indices = _indices[prefix];
        if (indices.ContainsKey(key))
            return;
        if (_freeSlots[prefix].TryPop(out int freeIndex))
        {
            slots[freeIndex] = key;
            indices[key] = freeIndex;
            return;
        }
        indices[key] = slots.Count;
        slots.Add(key);
    }

    internal void Remove(uint key)
    {
        uint prefix = key & 0xFFFF0000u;
        if (!_indices.TryGetValue(prefix, out Dictionary<uint, int>? indices)
            || !indices.Remove(key, out int slotIndex))
        {
            return;
        }
        _slots[prefix][slotIndex] = 0u;
        _freeSlots[prefix].Push(slotIndex);
        if (indices.Count != 0)
            return;
        _slots.Remove(prefix);
        _indices.Remove(prefix);
        _freeSlots.Remove(prefix);
    }

    internal List<uint>? SlotsForPrefix(uint prefix) =>
        _slots.TryGetValue(prefix & 0xFFFF0000u, out List<uint>? slots)
            ? slots
            : null;

    internal int InstalledKeyCountForPrefix(uint prefix) =>
        _indices.TryGetValue(prefix & 0xFFFF0000u, out var indices)
            ? indices.Count
            : 0;
}

internal sealed class CollisionWorldState
{
    internal Dictionary<uint, PhysicsEngine.LandblockPhysics> Landblocks { get; } = new();
    internal List<uint> LandblockSlots { get; } = new();
    internal Dictionary<uint, int> LandblockIndices { get; } = new();
    internal Stack<int> LandblockFreeSlots { get; } = new();
    internal ConcurrentDictionary<uint, CellPhysics> CellStruct { get; } = new();
    internal ConcurrentDictionary<uint, FlatCellStructureCollisionAsset>
        FlatCellStruct { get; } = new();
    internal ConcurrentDictionary<uint, FlatEnvCellTopology> FlatEnvCell { get; } = new();
    internal ConcurrentDictionary<uint, BuildingPhysics> Buildings { get; } = new();
    internal ConcurrentDictionary<uint, EnvCell> EnvCells { get; } = new();
    internal ConcurrentDictionary<uint, CellGraphTerrain> Terrain { get; } = new();
    internal ConcurrentDictionary<uint, ObjCell> OutdoorCells { get; } = new();
    internal Dictionary<uint, List<ShadowEntry>> ShadowCells { get; } = new();
    internal Dictionary<uint, List<uint>> ShadowEntityCells { get; } = new();
    internal HashSet<uint> SuspendedShadowEntities { get; } = new();
    internal Dictionary<uint, List<uint>> SuspendedShadowEntityCells { get; } = new();
    internal Dictionary<uint, HashSet<uint>> WithdrawnPrefixesByOwner { get; } = new();
    internal Dictionary<uint, IReadOnlyList<ShadowShape>> ShadowEntityShapes { get; } = new();
    internal Dictionary<uint, ShadowObjectRegistry.RegistrationRecord>
        ShadowEntityRegistrations { get; } = new();
    internal Dictionary<uint, ulong> ShadowOwnerVersions { get; } = new();
    internal Dictionary<uint, HashSet<uint>> ShadowOwnerPrefixes { get; } = new();
    internal Dictionary<uint, List<uint>> ShadowPrefixOwnerSlots { get; } = new();
    internal Dictionary<uint, Dictionary<uint, int>> ShadowPrefixOwnerIndices { get; } = new();
    internal Dictionary<uint, Stack<int>> ShadowPrefixFreeSlots { get; } = new();
    internal List<uint> ShadowOwnerSlots { get; } = new();
    internal Dictionary<uint, int> ShadowOwnerIndices { get; } = new();
    internal Stack<int> ShadowOwnerFreeSlots { get; } = new();

    internal Dictionary<uint, IReadOnlyList<ShadowShape>>
        ShadowEntityRetailPartArrays { get; } = new();
    internal Dictionary<uint, List<uint>> ShadowEntityRetailCellArrays { get; } = new();
    internal Dictionary<uint, RetailCellArrayRoute>
        ShadowEntityRetailCellArrayRoutes { get; } = new();
    internal Dictionary<uint, List<RetailPartEntry>> RetailPartEntriesByCell { get; } = new();

    internal Dictionary<uint, uint> ShadowChildParent { get; } = new();
    internal Dictionary<uint, List<uint>> ShadowParentChildren { get; } = new();
    internal Dictionary<uint, IReadOnlyList<ShadowShape>>
        ShadowChildPartArrays { get; } = new();

    // ── O1 per-prefix installed-key ledgers ────────────────────────────────
    // Every mutation of the five landblock-scoped world maps goes through the
    // typed helpers below so these ledgers stay exact. The seal's landblock-
    // replacement builders enumerate one prefix's keys instead of scanning the
    // whole resident map, and the retirement/removal paths retire one prefix
    // in O(prefix keys).
    internal PrefixKeyIndex CellStructKeys { get; } = new();
    internal PrefixKeyIndex FlatCellStructKeys { get; } = new();
    internal PrefixKeyIndex FlatEnvCellKeys { get; } = new();
    internal PrefixKeyIndex BuildingKeys { get; } = new();
    internal PrefixKeyIndex EnvCellKeys { get; } = new();

    internal void SetCellStruct(uint id, CellPhysics value)
    {
        CellStruct[id] = value;
        CellStructKeys.Add(id);
    }

    internal bool TryAddCellStruct(uint id, CellPhysics value)
    {
        if (!CellStruct.TryAdd(id, value))
            return false;
        CellStructKeys.Add(id);
        return true;
    }

    internal bool RemoveCellStruct(uint id)
    {
        if (!CellStruct.TryRemove(id, out _))
            return false;
        CellStructKeys.Remove(id);
        return true;
    }

    internal void SetFlatCellStruct(uint id, FlatCellStructureCollisionAsset value)
    {
        FlatCellStruct[id] = value;
        FlatCellStructKeys.Add(id);
    }

    internal bool TryAddFlatCellStruct(uint id, FlatCellStructureCollisionAsset value)
    {
        if (!FlatCellStruct.TryAdd(id, value))
            return false;
        FlatCellStructKeys.Add(id);
        return true;
    }

    internal bool RemoveFlatCellStruct(uint id)
    {
        if (!FlatCellStruct.TryRemove(id, out _))
            return false;
        FlatCellStructKeys.Remove(id);
        return true;
    }

    internal void SetFlatEnvCell(uint id, FlatEnvCellTopology value)
    {
        FlatEnvCell[id] = value;
        FlatEnvCellKeys.Add(id);
    }

    internal bool TryAddFlatEnvCell(uint id, FlatEnvCellTopology value)
    {
        if (!FlatEnvCell.TryAdd(id, value))
            return false;
        FlatEnvCellKeys.Add(id);
        return true;
    }

    internal bool RemoveFlatEnvCell(uint id)
    {
        if (!FlatEnvCell.TryRemove(id, out _))
            return false;
        FlatEnvCellKeys.Remove(id);
        return true;
    }

    internal void SetBuilding(uint id, BuildingPhysics value)
    {
        Buildings[id] = value;
        BuildingKeys.Add(id);
    }

    internal bool TryAddBuilding(uint id, BuildingPhysics value)
    {
        if (!Buildings.TryAdd(id, value))
            return false;
        BuildingKeys.Add(id);
        return true;
    }

    internal bool RemoveBuilding(uint id)
    {
        if (!Buildings.TryRemove(id, out _))
            return false;
        BuildingKeys.Remove(id);
        return true;
    }

    internal void SetEnvCell(uint id, EnvCell value)
    {
        EnvCells[id] = value;
        EnvCellKeys.Add(id);
    }

    internal bool TryAddEnvCell(uint id, EnvCell value)
    {
        if (!EnvCells.TryAdd(id, value))
            return false;
        EnvCellKeys.Add(id);
        return true;
    }

    internal bool RemoveEnvCell(uint id)
    {
        if (!EnvCells.TryRemove(id, out _))
            return false;
        EnvCellKeys.Remove(id);
        return true;
    }
}

internal sealed class CollisionWorldStateSlot
{
    private CollisionWorldState? _current = new();
    private bool _revoked;

    internal CollisionWorldStateSlot()
    {
    }

    internal CollisionWorldStateSlot(CollisionWorldState current)
    {
        _current = current ?? throw new ArgumentNullException(nameof(current));
    }

    internal CollisionWorldState Current
    {
        get
        {
            if (_revoked)
                throw new ObjectDisposedException("Transferred collision generation");
            return Volatile.Read(ref _current)
                ?? throw new ObjectDisposedException("Transferred collision generation");
        }
    }

    internal CollisionWorldState TransferTo(CollisionWorldStateSlot destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_revoked)
            throw new ObjectDisposedException("Transferred collision generation");
        CollisionWorldState transferred = _current
            ?? throw new ObjectDisposedException("Transferred collision generation");
        _revoked = true;
        Volatile.Write(ref destination._current, transferred);
        _current = null;
        return transferred;
    }

    internal void Revoke()
    {
        _revoked = true;
        _current = null;
    }

    internal CollisionWorldState Capture() => Current;
}
