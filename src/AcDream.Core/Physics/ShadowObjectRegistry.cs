using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Physics;

public sealed class ShadowObjectRegistry
{
    private CollisionWorldStateSlot _collisionWorld;
    private Dictionary<uint, List<ShadowEntry>> _cells =>
        _collisionWorld.Current.ShadowCells;
    private Dictionary<uint, List<uint>> _entityToCells =>
        _collisionWorld.Current.ShadowEntityCells; // for deregistration
    private HashSet<uint> _suspendedEntities =>
        _collisionWorld.Current.SuspendedShadowEntities;
    private Dictionary<uint, List<uint>> _suspendedEntityCells =>
        _collisionWorld.Current.SuspendedShadowEntityCells;
    private Dictionary<uint, HashSet<uint>> _withdrawnPrefixesByOwner =>
        _collisionWorld.Current.WithdrawnPrefixesByOwner;

    private Dictionary<uint, System.Collections.Generic.IReadOnlyList<ShadowShape>> _entityShapes =>
        _collisionWorld.Current.ShadowEntityShapes;

    private Dictionary<uint, System.Collections.Generic.IReadOnlyList<ShadowShape>> _entityRetailPartArrays =>
        _collisionWorld.Current.ShadowEntityRetailPartArrays;

    private Dictionary<uint, List<uint>> _retailCellArrays =>
        _collisionWorld.Current.ShadowEntityRetailCellArrays;

    private Dictionary<uint, RetailCellArrayRoute> _retailCellArrayRoutes =>
        _collisionWorld.Current.ShadowEntityRetailCellArrayRoutes;

    private Dictionary<uint, List<RetailPartEntry>> _retailPartEntriesByCell =>
        _collisionWorld.Current.RetailPartEntriesByCell;

    private Dictionary<uint, uint> _childParent =>
        _collisionWorld.Current.ShadowChildParent;

    private Dictionary<uint, List<uint>> _parentChildren =>
        _collisionWorld.Current.ShadowParentChildren;

    private Dictionary<uint, IReadOnlyList<ShadowShape>> _childPartArrays =>
        _collisionWorld.Current.ShadowChildPartArrays;

    private Dictionary<uint, RegistrationRecord> _entityReg =>
        _collisionWorld.Current.ShadowEntityRegistrations;
    private Dictionary<uint, ulong> _ownerVersions =>
        _collisionWorld.Current.ShadowOwnerVersions;
    private Dictionary<uint, HashSet<uint>> _ownerPrefixes =>
        _collisionWorld.Current.ShadowOwnerPrefixes;
    private Dictionary<uint, List<uint>> _prefixOwnerSlots =>
        _collisionWorld.Current.ShadowPrefixOwnerSlots;
    private Dictionary<uint, Dictionary<uint, int>> _prefixOwnerIndices =>
        _collisionWorld.Current.ShadowPrefixOwnerIndices;
    private Dictionary<uint, Stack<int>> _prefixFreeSlots =>
        _collisionWorld.Current.ShadowPrefixFreeSlots;
    private List<uint> _ownerSlots =>
        _collisionWorld.Current.ShadowOwnerSlots;
    private Dictionary<uint, int> _ownerIndices =>
        _collisionWorld.Current.ShadowOwnerIndices;
    private Stack<int> _ownerFreeSlots =>
        _collisionWorld.Current.ShadowOwnerFreeSlots;
    private readonly HashSet<uint> _prefixScratch = new();
    private readonly List<uint> _removedPrefixScratch = new();
    private ulong _mutationRevision;
    private ulong _nextPreparedSetPositionCommitId;
    private ulong _lastAppliedSetPositionCommitId;
    private readonly HashSet<ulong> _pendingSetPositionDispatches = [];
    private long _setPositionDispatchFailureCount;
    internal event Action<uint, ulong>? OwnerMutated;
    internal event Action<uint, uint>? OwnerPrefixMembershipChanged;

    public ShadowObjectRegistry()
        : this(new CollisionWorldStateSlot())
    {
    }

    internal ShadowObjectRegistry(CollisionWorldStateSlot collisionWorld)
    {
        _collisionWorld = collisionWorld
            ?? throw new ArgumentNullException(nameof(collisionWorld));
    }

    internal void AttachCollisionWorld(CollisionWorldStateSlot collisionWorld)
    {
        ArgumentNullException.ThrowIfNull(collisionWorld);
        if (_cells.Count != 0 || _entityReg.Count != 0)
        {
            throw new InvalidOperationException(
                "A populated shadow registry cannot change collision roots.");
        }
        _collisionWorld = collisionWorld;
        AdvanceMutationRevision();
    }

    internal sealed record RegistrationRecord(
        uint SeedCellId,
        Vector3 EntityWorldPos,
        Quaternion EntityWorldRot,
        uint State,
        EntityCollisionFlags Flags,
        bool IsStatic,
        bool IsMultiPart,
        // Single-shape fields (IsMultiPart == false):
        uint GfxObjId,
        float Radius,
        ShadowCollisionType CollisionType,
        float CylHeight,
        float Scale);

    internal ulong GetOwnerVersion(uint entityId) =>
        _ownerVersions.TryGetValue(entityId, out ulong version)
            ? version
            : 0UL;

    internal ulong MutationRevision => _mutationRevision;

    private void AdvanceMutationRevision() =>
        _mutationRevision = checked(_mutationRevision + 1UL);

    private void BumpOwnerVersion(uint entityId)
    {
        AdvanceMutationRevision();
        ulong version = checked(GetOwnerVersion(entityId) + 1UL);
        _ownerVersions[entityId] = version;
        RefreshOwnerPrefixIndex(entityId);
        OwnerMutated?.Invoke(entityId, version);
    }

    internal RetainedRefloodOwnerScan CreateRetainedRefloodOwnerScan(
        uint landblockId)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        _prefixOwnerSlots.TryGetValue(prefix, out List<uint>? slots);
        return new RetainedRefloodOwnerScan(this, prefix, slots);
    }

    internal sealed class RetainedRefloodOwnerScan : IDisposable
    {
        private readonly ShadowObjectRegistry _owner;
        private readonly uint _prefix;
        private readonly List<uint>? _slots;
        private readonly int _limit;
        private int _index;
        private bool _completed;

        internal RetainedRefloodOwnerScan(
            ShadowObjectRegistry owner,
            uint prefix,
            List<uint>? slots)
        {
            _owner = owner;
            _prefix = prefix;
            _slots = slots;
            _limit = slots?.Count ?? 0;
        }

        internal RetainedRefloodOwnerScanStep Advance()
        {
            if (_completed)
            {
                return new RetainedRefloodOwnerScanStep(
                    Completed: true,
                    HasOwner: false,
                    OwnerId: 0u);
            }
            if (_slots is null || _index >= _limit)
            {
                _completed = true;
                return new RetainedRefloodOwnerScanStep(
                    Completed: true,
                    HasOwner: false,
                    OwnerId: 0u);
            }

            uint ownerId = _slots[_index++];
            bool retained = _owner.IsRetainedRefloodOwner(ownerId, _prefix);
            return new RetainedRefloodOwnerScanStep(
                Completed: false,
                HasOwner: retained,
                OwnerId: retained ? ownerId : 0u);
        }

        public void Dispose() { }
    }

    private void RefreshOwnerPrefixIndex(uint entityId)
    {
        if (!_entityReg.ContainsKey(entityId))
        {
            RemoveOwnerPrefixMembership(entityId);
            return;
        }
        EnsureOwnerSlot(entityId);
        _prefixScratch.Clear();
        if (_entityReg.TryGetValue(entityId, out RegistrationRecord? registration))
            _prefixScratch.Add(registration.SeedCellId & 0xFFFF0000u);
        if (_entityToCells.TryGetValue(entityId, out List<uint>? cells))
        {
            for (int index = 0; index < cells.Count; index++)
                _prefixScratch.Add(cells[index] & 0xFFFF0000u);
        }
        if (_withdrawnPrefixesByOwner.TryGetValue(
                entityId,
                out HashSet<uint>? withdrawn))
        {
            foreach (uint prefix in withdrawn)
                _prefixScratch.Add(prefix & 0xFFFF0000u);
        }

        if (!_ownerPrefixes.TryGetValue(entityId, out HashSet<uint>? current))
        {
            current = new HashSet<uint>();
            _ownerPrefixes[entityId] = current;
        }

        _removedPrefixScratch.Clear();
        foreach (uint prefix in current)
        {
            if (!_prefixScratch.Contains(prefix))
                _removedPrefixScratch.Add(prefix);
        }
        for (int index = 0; index < _removedPrefixScratch.Count; index++)
        {
            uint prefix = _removedPrefixScratch[index];
            current.Remove(prefix);
            if (_prefixOwnerIndices.TryGetValue(
                    prefix,
                    out Dictionary<uint, int>? indices)
                && indices.Remove(entityId, out int slotIndex))
            {
                _prefixOwnerSlots[prefix][slotIndex] = 0u;
                _prefixFreeSlots[prefix].Push(slotIndex);
                ReleaseEmptyPrefixContainer(prefix, indices);
            }
            OwnerPrefixMembershipChanged?.Invoke(entityId, prefix);
        }

        foreach (uint prefix in _prefixScratch)
        {
            if (!current.Add(prefix))
                continue;
            if (!_prefixOwnerSlots.TryGetValue(prefix, out List<uint>? slots))
            {
                slots = new List<uint>();
                _prefixOwnerSlots[prefix] = slots;
                _prefixOwnerIndices[prefix] = new Dictionary<uint, int>();
                _prefixFreeSlots[prefix] = new Stack<int>();
            }
            Dictionary<uint, int> indices = _prefixOwnerIndices[prefix];
            if (indices.ContainsKey(entityId))
                continue;
            Stack<int> free = _prefixFreeSlots[prefix];
            if (free.TryPop(out int freeIndex))
            {
                slots[freeIndex] = entityId;
                indices[entityId] = freeIndex;
            }
            else
            {
                indices[entityId] = slots.Count;
                slots.Add(entityId);
            }
            OwnerPrefixMembershipChanged?.Invoke(entityId, prefix);
        }

    }

    private void RemoveOwnerPrefixMembership(uint entityId)
    {
        if (_ownerPrefixes.Remove(entityId, out HashSet<uint>? prefixes))
        {
            foreach (uint prefix in prefixes)
            {
                if (!_prefixOwnerIndices.TryGetValue(
                        prefix,
                        out Dictionary<uint, int>? indices)
                    || !indices.Remove(entityId, out int slotIndex))
                {
                    continue;
                }

                _prefixOwnerSlots[prefix][slotIndex] = 0u;
                _prefixFreeSlots[prefix].Push(slotIndex);
                ReleaseEmptyPrefixContainer(prefix, indices);
                OwnerPrefixMembershipChanged?.Invoke(entityId, prefix);
            }
        }
        if (_ownerIndices.Remove(entityId, out int ownerSlot))
        {
            _ownerSlots[ownerSlot] = 0u;
            _ownerFreeSlots.Push(ownerSlot);
        }
    }

    private void EnsureOwnerSlot(uint entityId)
    {
        if (_ownerIndices.ContainsKey(entityId))
            return;
        if (_ownerFreeSlots.TryPop(out int freeIndex))
        {
            _ownerSlots[freeIndex] = entityId;
            _ownerIndices[entityId] = freeIndex;
            return;
        }
        _ownerIndices[entityId] = _ownerSlots.Count;
        _ownerSlots.Add(entityId);
    }

    private void ReleaseEmptyPrefixContainer(
        uint prefix,
        Dictionary<uint, int> indices)
    {
        if (indices.Count != 0)
            return;
        _prefixOwnerSlots.Remove(prefix);
        _prefixOwnerIndices.Remove(prefix);
        _prefixFreeSlots.Remove(prefix);
    }

    internal readonly record struct RetainedRefloodOwnerScanStep(
        bool Completed,
        bool HasOwner,
        uint OwnerId);

    public PhysicsDataCache? DataCache { get; set; }

    private PhysicsDataCache _fallbackCache => _fallback ??= new PhysicsDataCache();
    private PhysicsDataCache? _fallback;
    private PhysicsDataCache FloodCache => DataCache ?? _fallbackCache;

    private (IReadOnlyList<uint> Cells, RetailCellArrayRoute Route) ComputeContractACellArray(
        uint seedCellId,
        Vector3 worldPos,
        Quaternion worldRot,
        uint state,
        IReadOnlyList<ShadowShape> collisionShapes,
        IReadOnlyList<ShadowShape> partArray,
        bool isStatic)
    {
        bool hasCylsphere = false;
        for (int i = 0; i < collisionShapes.Count; i++)
        {
            if (collisionShapes[i].CollisionType == ShadowCollisionType.Cylinder)
            {
                hasCylsphere = true;
                break;
            }
        }
        bool cylsphereRoute = (state & 0x10000u) == 0u && hasCylsphere;

        if (cylsphereRoute)
        {
            List<DatReaderWriter.Types.Sphere> cylSpheres =
                BuildFloodSpheres(worldPos, worldRot, collisionShapes);
            IReadOnlyList<uint> cells = CellTransit.BuildShadowCellSet(
                FloodCache, seedCellId, cylSpheres, cylSpheres.Count, isStatic);
            return (cells, RetailCellArrayRoute.Cylsphere);
        }
        else
        {
            List<ShadowPartBox> boxes =
                BuildFloodPartBoxes(worldPos, worldRot, partArray);
            List<DatReaderWriter.Types.Sphere> spheres =
                BuildBspPartSpheres(worldPos, worldRot, partArray);
            IReadOnlyList<uint> cells = CellTransit.BuildShadowCellSetFromParts(
                FloodCache, seedCellId, boxes, spheres, isStatic);
            return (cells, RetailCellArrayRoute.BoundingBox);
        }
    }

    private void PublishRetailCellArray(
        uint entityId,
        IReadOnlyList<uint> cellArray,
        RetailCellArrayRoute route,
        IReadOnlyList<ShadowShape> partArray)
    {
        if (_childParent.ContainsKey(entityId))
        {
            if (partArray.Count != 0)
                _childPartArrays[entityId] = partArray;
            _retailCellArrayRoutes[entityId] = route;
            PublishChildEntries(entityId);
            return;
        }
        if (_retailCellArrays.TryGetValue(entityId, out List<uint>? previousCells))
        {
            RemoveRetailPartEntriesFromCells(entityId, previousCells);
            _retailCellArrays.Remove(entityId);
        }
        _retailCellArrayRoutes[entityId] = route;
        if (cellArray.Count == 0)
        {
            RepublishAttachedChildren(entityId);
            return;
        }

        var orderedCells = new List<uint>(cellArray.Count);
        for (int i = 0; i < cellArray.Count; i++)
            orderedCells.Add(cellArray[i]);
        _retailCellArrays[entityId] = orderedCells;
        PublishRetailPartEntries(entityId, orderedCells, partArray);
        RepublishAttachedChildren(entityId);
    }

    private void PublishRetailProductFromExactCells(
        uint entityId,
        IReadOnlyList<uint> exactCells)
    {
        if (!_entityRetailPartArrays.TryGetValue(
                entityId,
                out IReadOnlyList<ShadowShape>? partArray)
            || partArray.Count == 0)
        {
            return;
        }
        RetailCellArrayRoute route = _retailCellArrayRoutes.TryGetValue(
                entityId,
                out RetailCellArrayRoute existingRoute)
            ? existingRoute
            : RetailCellArrayRoute.None;
        PublishRetailCellArray(entityId, exactCells, route, partArray);
    }

    private void ClearRetailCellArray(uint entityId)
    {
        if (_retailCellArrays.TryGetValue(entityId, out List<uint>? cells))
        {
            RemoveRetailPartEntriesFromCells(entityId, cells);
            _retailCellArrays.Remove(entityId);
        }
        _retailCellArrayRoutes.Remove(entityId);
        _entityRetailPartArrays.Remove(entityId);
    }

    private void RemoveRetailPartEntriesFromCells(
        uint entityId,
        IReadOnlyList<uint> cellIds)
    {
        for (int i = 0; i < cellIds.Count; i++)
        {
            if (_retailPartEntriesByCell.TryGetValue(
                    cellIds[i],
                    out List<RetailPartEntry>? entries))
            {
                RemoveOwnerPartRows(entries, entityId);
                if (entries.Count == 0)
                    _retailPartEntriesByCell.Remove(cellIds[i]);
            }
        }
    }

    private static void RemoveOwnerPartRows(
        List<RetailPartEntry> entries,
        uint entityId)
    {
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].EntityId == entityId)
                entries.RemoveAt(index);
        }
    }

    private static ShadowEntry[] CollectOwnerRows(
        List<ShadowEntry> entries,
        uint entityId)
    {
        int count = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].EntityId == entityId)
                count++;
        }
        if (count == 0)
            return Array.Empty<ShadowEntry>();
        var rows = new ShadowEntry[count];
        int written = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].EntityId == entityId)
                rows[written++] = entries[index];
        }
        return rows;
    }

    private static RetailPartEntry[] CollectOwnerPartRows(
        List<RetailPartEntry> entries,
        uint entityId)
    {
        int count = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].EntityId == entityId)
                count++;
        }
        if (count == 0)
            return Array.Empty<RetailPartEntry>();
        var rows = new RetailPartEntry[count];
        int written = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].EntityId == entityId)
                rows[written++] = entries[index];
        }
        return rows;
    }

    private void PublishRetailPartEntries(
        uint entityId,
        IReadOnlyList<uint> orderedCells,
        IReadOnlyList<ShadowShape> partArray)
    {
        bool clipPlanesRequired = orderedCells.Count > 1;
        for (int cellIndex = 0; cellIndex < orderedCells.Count; cellIndex++)
        {
            uint cellId = orderedCells[cellIndex];
            if (!_retailPartEntriesByCell.TryGetValue(
                    cellId,
                    out List<RetailPartEntry>? entries))
            {
                entries = new List<RetailPartEntry>();
                _retailPartEntriesByCell[cellId] = entries;
            }
            for (int partIndex = 0; partIndex < partArray.Count; partIndex++)
            {
                entries.Add(new RetailPartEntry(
                    entityId,
                    partIndex,
                    partArray[partIndex].GfxObjId,
                    cellId,
                    clipPlanesRequired));
            }
        }
    }

    public bool TryGetRetailCellArray(
        uint entityId,
        out IReadOnlyList<uint> cells)
    {
        if (_retailCellArrays.TryGetValue(entityId, out List<uint>? list))
        {
            cells = list;
            return true;
        }
        cells = Array.Empty<uint>();
        return false;
    }

    public IReadOnlyList<RetailPartEntry> GetRetailPartEntriesInCell(uint cellId) =>
        _retailPartEntriesByCell.TryGetValue(cellId, out List<RetailPartEntry>? entries)
            ? entries
            : Array.Empty<RetailPartEntry>();

    public RetailCellArrayRoute GetRetailCellArrayRoute(uint entityId) =>
        _retailCellArrayRoutes.TryGetValue(entityId, out RetailCellArrayRoute route)
            ? route
            : RetailCellArrayRoute.None;

    private const int MaxAttachChainDepth = 64;

    public bool AttachChild(
        uint childEntityId,
        uint rootEntityId,
        IReadOnlyList<ShadowShape> childPartArray)
    {
        ArgumentNullException.ThrowIfNull(childPartArray);
        if (!TryValidateAttach(childEntityId, rootEntityId))
            return false;

        if (_childParent.ContainsKey(childEntityId))
            DetachChildCore(childEntityId, removeFromParentList: true);

        _childParent[childEntityId] = rootEntityId;
        if (!_parentChildren.TryGetValue(rootEntityId, out List<uint>? siblings))
        {
            siblings = new List<uint>();
            _parentChildren[rootEntityId] = siblings;
        }
        siblings.Add(childEntityId);
        _childPartArrays[childEntityId] = childPartArray;

        PublishChildEntries(childEntityId);
        AdvanceMutationRevision();
        return true;
    }

    public bool DetachChild(uint childEntityId) =>
        DetachChildCore(childEntityId, removeFromParentList: true);

    private bool DetachChildCore(uint childEntityId, bool removeFromParentList)
    {
        if (!_childParent.TryGetValue(childEntityId, out uint parentId))
            return false;

        if (_parentChildren.TryGetValue(childEntityId, out List<uint>? grandchildren)
            && grandchildren.Count > 0)
        {
            uint[] toDetach = grandchildren.ToArray();
            for (int i = 0; i < toDetach.Length; i++)
                DetachChildCore(toDetach[i], removeFromParentList: false);
            _parentChildren.Remove(childEntityId);
        }

        if (_retailCellArrays.TryGetValue(childEntityId, out List<uint>? cells))
        {
            RemoveRetailPartEntriesFromCells(childEntityId, cells);
            _retailCellArrays.Remove(childEntityId);
        }
        _childPartArrays.Remove(childEntityId);
        _childParent.Remove(childEntityId);

        if (removeFromParentList
            && _parentChildren.TryGetValue(parentId, out List<uint>? siblings))
        {
            siblings.Remove(childEntityId);
            if (siblings.Count == 0)
                _parentChildren.Remove(parentId);
        }
        AdvanceMutationRevision(); // see AttachChild (arch F2)
        return true;
    }

    private bool TryValidateAttach(uint childEntityId, uint parentId)
    {
        if (childEntityId == parentId)
            return false;

        uint current = parentId;
        for (int depth = 0; depth < MaxAttachChainDepth; depth++)
        {
            if (!_childParent.TryGetValue(current, out uint next))
                return true;
            if (next == childEntityId)
                return false;
            current = next;
        }
        return false;
    }

    private uint ResolveAttachRoot(uint entityId)
    {
        uint current = entityId;
        for (int depth = 0; depth < MaxAttachChainDepth; depth++)
        {
            if (!_childParent.TryGetValue(current, out uint parent))
                return current;
            current = parent;
        }
        return current;
    }

    private void PublishChildEntries(uint childEntityId)
    {
        if (_retailCellArrays.TryGetValue(childEntityId, out List<uint>? previousCells))
        {
            RemoveRetailPartEntriesFromCells(childEntityId, previousCells);
            _retailCellArrays.Remove(childEntityId);
        }
        if (!_childPartArrays.TryGetValue(childEntityId, out IReadOnlyList<ShadowShape>? partArray))
            return;

        uint root = ResolveAttachRoot(childEntityId);
        if (root == childEntityId
            || !_retailCellArrays.TryGetValue(root, out List<uint>? rootCells)
            || rootCells.Count == 0)
        {
            return;
        }

        _retailCellArrays[childEntityId] = rootCells;
        PublishRetailPartEntries(childEntityId, rootCells, partArray);
    }

    private void RepublishAttachedChildren(uint rootId)
    {
        if (!_parentChildren.TryGetValue(rootId, out List<uint>? children)
            || children.Count == 0)
        {
            return;
        }
        for (int i = 0; i < children.Count; i++)
        {
            uint childId = children[i];
            PublishChildEntries(childId);
            RepublishAttachedChildren(childId);
        }
    }

    public void Register(uint entityId, uint gfxObjId, Vector3 worldPos, Quaternion rotation,
                         float radius, float worldOffsetX, float worldOffsetY, uint landblockId,
                         ShadowCollisionType collisionType = ShadowCollisionType.BSP,
                         float cylHeight = 0f, float scale = 1.0f,
                         uint state = 0u,
                         EntityCollisionFlags flags = EntityCollisionFlags.None,
                         uint seedCellId = 0u,
                         bool isStatic = true,
                         bool publishMutation = true,
                         IReadOnlyList<ShadowShape>? partArray = null)
    {
        uint seed = seedCellId != 0u
            ? seedCellId
            : DeriveOutdoorSeed(worldPos, worldOffsetX, worldOffsetY, landblockId);
        if (seed == 0u) return;

        bool hasRetailPartArray = partArray is not null && partArray.Count != 0;

        IReadOnlyList<uint> cellSet;
        RetailCellArrayRoute retailRoute = RetailCellArrayRoute.None;
        if (hasRetailPartArray)
        {
            IReadOnlyList<ShadowShape> collisionShapes =
                collisionType == ShadowCollisionType.Cylinder
                    ? new[]
                    {
                        ShadowShape.Cylinder(
                            gfxObjId, Vector3.Zero, Quaternion.Identity, scale, radius, cylHeight),
                    }
                    : Array.Empty<ShadowShape>();
            (cellSet, retailRoute) = ComputeContractACellArray(
                seed, worldPos, rotation, state, collisionShapes, partArray!, isStatic);
        }
        else
        {
            var spheres = new[]
            {
                new DatReaderWriter.Types.Sphere { Origin = worldPos, Radius = radius },
            };
            cellSet = CellTransit.BuildShadowCellSet(
                FloodCache, seed, spheres, spheres.Length, isStatic);
        }
        if (cellSet.Count == 0) return;

        DeregisterCore(entityId, publishMutation: false);

        var entry = new ShadowEntry(entityId, gfxObjId, worldPos, rotation, radius,
                                    collisionType, cylHeight, scale, state, flags);

        var cellIds = new List<uint>(cellSet.Count);
        foreach (uint cellId in cellSet)
        {
            AddEntryToCell(entry, cellId);
            cellIds.Add(cellId);
        }

        _entityToCells[entityId] = cellIds;
        _entityReg[entityId] = new RegistrationRecord(
            seed, worldPos, rotation, state, flags, isStatic,
            IsMultiPart: false, gfxObjId, radius, collisionType, cylHeight, scale);
        if (publishMutation)
            BumpOwnerVersion(entityId);
        else
            RefreshOwnerPrefixIndex(entityId);

        if (hasRetailPartArray)
        {
            _entityRetailPartArrays[entityId] = partArray!;
            PublishRetailCellArray(entityId, cellSet, retailRoute, partArray!);
        }
    }

    public void RegisterMultiPart(
        uint entityId,
        Vector3 entityWorldPos,
        Quaternion entityWorldRot,
        System.Collections.Generic.IReadOnlyList<ShadowShape> shapes,
        uint state,
        EntityCollisionFlags flags,
        float worldOffsetX, float worldOffsetY, uint landblockId,
        uint seedCellId = 0u,
        bool isStatic = false,
        bool publishMutation = true,
        IReadOnlyList<ShadowShape>? partArray = null)
    {
        if (shapes.Count == 0)
        {
            if (partArray is { Count: > 0 })
            {
                RegisterRenderOnly(
                    entityId, entityWorldPos, entityWorldRot, state, flags,
                    worldOffsetX, worldOffsetY, landblockId, seedCellId,
                    isStatic, publishMutation, partArray);
            }
            else
            {
                Deregister(entityId);
            }
            return;
        }

        // Flood FIRST — keep-when-empty, see Register.
        uint seed = seedCellId != 0u
            ? seedCellId
            : DeriveOutdoorSeed(entityWorldPos, worldOffsetX, worldOffsetY, landblockId);
        if (seed == 0u) return;

        bool hasRetailPartArray = partArray is not null && partArray.Count != 0;

        IReadOnlyList<uint> cellSet;
        RetailCellArrayRoute retailRoute = RetailCellArrayRoute.None;
        if (hasRetailPartArray)
        {
            (cellSet, retailRoute) = ComputeContractACellArray(
                seed, entityWorldPos, entityWorldRot, state, shapes, partArray!, isStatic);
        }
        else
        {
            bool hasBsp = false;
            for (int i = 0; i < shapes.Count; i++)
            {
                if (shapes[i].CollisionType == ShadowCollisionType.BSP)
                {
                    hasBsp = true;
                    break;
                }
            }

            if (hasBsp)
            {
                var partBoxes = BuildFloodPartBoxes(entityWorldPos, entityWorldRot, shapes);
                var partSpheres = BuildBspPartSpheres(entityWorldPos, entityWorldRot, shapes);
                cellSet = CellTransit.BuildShadowCellSetFromParts(
                    FloodCache, seed, partBoxes, partSpheres, isStatic);
            }
            else
            {
                var floodSpheres = BuildFloodSpheres(entityWorldPos, entityWorldRot, shapes);
                cellSet = CellTransit.BuildShadowCellSet(
                    FloodCache, seed, floodSpheres, floodSpheres.Count, isStatic);
            }
        }
        if (cellSet.Count == 0) return;

        DeregisterCore(entityId, publishMutation: false);
        _entityShapes[entityId] = shapes;
        var allCells = new List<uint>(cellSet.Count);

        foreach (var shape in shapes)
        {
            var rotatedLocal = Vector3.Transform(shape.LocalPosition, entityWorldRot);
            var partWorldPos = entityWorldPos + rotatedLocal;
            var partWorldRot = entityWorldRot * shape.LocalRotation;

            var entry = new ShadowEntry(
                EntityId:      entityId,
                GfxObjId:      shape.GfxObjId,
                Position:      partWorldPos,
                Rotation:      partWorldRot,
                Radius:        shape.Radius,
                CollisionType: shape.CollisionType,
                CylHeight:     shape.CylHeight,
                Scale:         shape.Scale,
                State:         state,
                Flags:         flags,
                LocalPosition: shape.LocalPosition,
                LocalRotation: shape.LocalRotation);

            foreach (uint cellId in cellSet)
                AddEntryToCell(entry, cellId);
        }

        foreach (uint cellId in cellSet)
            allCells.Add(cellId);

        _entityToCells[entityId] = allCells;
        _entityReg[entityId] = new RegistrationRecord(
            seed, entityWorldPos, entityWorldRot, state, flags, isStatic,
            IsMultiPart: true, GfxObjId: 0u, Radius: 0f,
            CollisionType: ShadowCollisionType.BSP, CylHeight: 0f, Scale: 1f);
        if (publishMutation)
            BumpOwnerVersion(entityId);
        else
            RefreshOwnerPrefixIndex(entityId);

        if (hasRetailPartArray)
        {
            _entityRetailPartArrays[entityId] = partArray!;
            PublishRetailCellArray(entityId, cellSet, retailRoute, partArray!);
        }
    }

    private void RegisterRenderOnly(
        uint entityId,
        Vector3 entityWorldPos,
        Quaternion entityWorldRot,
        uint state,
        EntityCollisionFlags flags,
        float worldOffsetX,
        float worldOffsetY,
        uint landblockId,
        uint seedCellId,
        bool isStatic,
        bool publishMutation,
        IReadOnlyList<ShadowShape> partArray)
    {
        // Flood FIRST — keep-when-empty, see Register.
        uint seed = seedCellId != 0u
            ? seedCellId
            : DeriveOutdoorSeed(entityWorldPos, worldOffsetX, worldOffsetY, landblockId);
        if (seed == 0u) return;

        (IReadOnlyList<uint> cellSet, RetailCellArrayRoute retailRoute) =
            ComputeContractACellArray(
                seed,
                entityWorldPos,
                entityWorldRot,
                state,
                collisionShapes: Array.Empty<ShadowShape>(),
                partArray,
                isStatic);
        if (cellSet.Count == 0) return; // keep-when-empty (pc:283540).

        DeregisterCore(entityId, publishMutation: false);
        _entityShapes[entityId] = Array.Empty<ShadowShape>();
        _entityReg[entityId] = new RegistrationRecord(
            seed, entityWorldPos, entityWorldRot, state, flags, isStatic,
            IsMultiPart: true, GfxObjId: 0u, Radius: 0f,
            CollisionType: ShadowCollisionType.BSP, CylHeight: 0f, Scale: 1f);
        if (publishMutation)
            BumpOwnerVersion(entityId);
        else
            RefreshOwnerPrefixIndex(entityId);

        _entityRetailPartArrays[entityId] = partArray;
        PublishRetailCellArray(entityId, cellSet, retailRoute, partArray);
    }

    public void ReplaceMultiPartPayload(
        uint entityId,
        Vector3 entityWorldPos,
        Quaternion entityWorldRot,
        System.Collections.Generic.IReadOnlyList<ShadowShape> shapes,
        uint state,
        EntityCollisionFlags flags,
        float worldOffsetX,
        float worldOffsetY,
        uint landblockId,
        uint seedCellId = 0u,
        bool isStatic = false,
        bool suspendIfNew = false,
        IReadOnlyList<ShadowShape>? partArray = null)
    {
        if (!_entityReg.TryGetValue(entityId, out RegistrationRecord? prior)
            || !prior.IsMultiPart)
        {
            if (shapes.Count == 0 && (partArray is null || partArray.Count == 0))
                return;
            RegisterMultiPart(
                entityId,
                entityWorldPos,
                entityWorldRot,
                shapes,
                state,
                flags,
                worldOffsetX,
                worldOffsetY,
                landblockId,
                seedCellId,
                isStatic,
                partArray: partArray);
            if (suspendIfNew)
                Suspend(entityId);
            return;
        }

        bool suspended = _suspendedEntities.Contains(entityId);
        _entityShapes[entityId] = shapes;
        _entityReg[entityId] = prior with
        {
            EntityWorldPos = entityWorldPos,
            EntityWorldRot = entityWorldRot,
            State = state,
            Flags = flags,
        };

        if (partArray is { Count: > 0 })
        {
            _entityRetailPartArrays[entityId] = partArray;
            if (_retailCellArrays.TryGetValue(entityId, out List<uint>? retailCells)
                && retailCells.Count != 0)
            {
                RemoveRetailPartEntriesFromCells(entityId, retailCells);
                PublishRetailPartEntries(entityId, retailCells, partArray);
                RepublishAttachedChildren(entityId);
            }
        }

        if (suspended || !_entityToCells.TryGetValue(entityId, out List<uint>? cells))
        {
            BumpOwnerVersion(entityId);
            return;
        }

        foreach (uint cellId in cells)
        {
            if (_cells.TryGetValue(cellId, out List<ShadowEntry>? entries))
                entries.RemoveAll(entry => entry.EntityId == entityId);
        }

        foreach (ShadowShape shape in shapes)
        {
            Vector3 partWorldPos = entityWorldPos
                + Vector3.Transform(shape.LocalPosition, entityWorldRot);
            Quaternion partWorldRot = entityWorldRot * shape.LocalRotation;
            var entry = new ShadowEntry(
                EntityId: entityId,
                GfxObjId: shape.GfxObjId,
                Position: partWorldPos,
                Rotation: partWorldRot,
                Radius: shape.Radius,
                CollisionType: shape.CollisionType,
                CylHeight: shape.CylHeight,
                Scale: shape.Scale,
                State: state,
                Flags: flags,
                LocalPosition: shape.LocalPosition,
                LocalRotation: shape.LocalRotation);
            foreach (uint cellId in cells)
                AddEntryToCell(entry, cellId);
        }
        BumpOwnerVersion(entityId);
    }

    private static List<DatReaderWriter.Types.Sphere> BuildFloodSpheres(
        Vector3 entityWorldPos,
        Quaternion entityWorldRot,
        System.Collections.Generic.IReadOnlyList<ShadowShape> shapes)
    {
        const int RetailSphereCap = 10;

        var spheres = new List<DatReaderWriter.Types.Sphere>();
        bool anyCyl = false;
        foreach (var s in shapes)
        {
            if (s.CollisionType == ShadowCollisionType.Cylinder) anyCyl = true;
        }

        ShadowCollisionType only =
            anyCyl ? ShadowCollisionType.Cylinder : ShadowCollisionType.Sphere;

        int cap = only == ShadowCollisionType.Cylinder ? RetailSphereCap : int.MaxValue;

        foreach (var s in shapes)
        {
            if (s.CollisionType != only)
                continue;
            if (spheres.Count >= cap)
                break;

            var partWorldPos = entityWorldPos + Vector3.Transform(s.LocalPosition, entityWorldRot);
            var partWorldRot = entityWorldRot * s.LocalRotation;
            var world = partWorldPos + Vector3.Transform(s.BoundsCenter, partWorldRot);
            spheres.Add(new DatReaderWriter.Types.Sphere
            {
                Origin = world,
                Radius = s.Radius,
            });
        }

        return spheres;
    }

    private static List<ShadowPartBox> BuildFloodPartBoxes(
        Vector3 entityWorldPos,
        Quaternion entityWorldRot,
        System.Collections.Generic.IReadOnlyList<ShadowShape> shapes)
    {
        var boxes = new List<ShadowPartBox>(shapes.Count);
        foreach (var s in shapes)
        {
            if (s.CollisionType != ShadowCollisionType.BSP)
                continue;
            boxes.Add(ShadowPartBox.FromShape(s, entityWorldPos, entityWorldRot));
        }
        return boxes;
    }

    private static List<DatReaderWriter.Types.Sphere> BuildBspPartSpheres(
        Vector3 entityWorldPos,
        Quaternion entityWorldRot,
        System.Collections.Generic.IReadOnlyList<ShadowShape> shapes)
    {
        var spheres = new List<DatReaderWriter.Types.Sphere>(shapes.Count);
        foreach (var s in shapes)
        {
            if (s.CollisionType != ShadowCollisionType.BSP)
                continue;
            var partWorldPos = entityWorldPos + Vector3.Transform(s.LocalPosition, entityWorldRot);
            var partWorldRot = entityWorldRot * s.LocalRotation;
            spheres.Add(new DatReaderWriter.Types.Sphere
            {
                Origin = partWorldPos + Vector3.Transform(s.BoundsCenter, partWorldRot),
                Radius = s.Radius,
            });
        }
        return spheres;
    }

    private static uint DeriveOutdoorSeed(
        Vector3 worldPos, float worldOffsetX, float worldOffsetY, uint landblockId)
    {
        if (landblockId == 0u) return 0u;
        float localX = worldPos.X - worldOffsetX;
        float localY = worldPos.Y - worldOffsetY;
        int cx = (int)System.Math.Clamp(localX / 24f, 0f, 7f);
        int cy = (int)System.Math.Clamp(localY / 24f, 0f, 7f);
        uint lbPrefix = landblockId & 0xFFFF0000u;
        return lbPrefix | (uint)(cx * 8 + cy + 1);
    }

    private void AddEntryToCell(ShadowEntry entry, uint cellId)
    {
        if (!_cells.TryGetValue(cellId, out var list))
        {
            list = new List<ShadowEntry>();
            _cells[cellId] = list;
        }
        list.Add(entry);
    }

    public void UpdatePosition(uint entityId, Vector3 worldPos, Quaternion rotation,
                               float worldOffsetX, float worldOffsetY, uint landblockId,
                               uint seedCellId = 0u)
    {
        if (!_entityReg.TryGetValue(entityId, out var reg))
            return;

        if (seedCellId == 0u
            && DeriveOutdoorSeed(worldPos, worldOffsetX, worldOffsetY, landblockId) == 0u)
            return;

        _entityRetailPartArrays.TryGetValue(
            entityId,
            out IReadOnlyList<ShadowShape>? retainedPartArray);

        if (reg.IsMultiPart && _entityShapes.TryGetValue(entityId, out var shapes))
        {
            RegisterMultiPart(entityId, worldPos, rotation, shapes,
                              reg.State, reg.Flags, worldOffsetX, worldOffsetY, landblockId,
                              seedCellId, reg.IsStatic, partArray: retainedPartArray);
            return;
        }

        Register(entityId, reg.GfxObjId, worldPos, rotation, reg.Radius,
                 worldOffsetX, worldOffsetY, landblockId,
                 reg.CollisionType, reg.CylHeight, reg.Scale,
                 reg.State, reg.Flags, seedCellId, reg.IsStatic,
                 partArray: retainedPartArray);
    }

    internal void CommitSetPosition(
        uint entityId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        uint seedCellId,
        float worldOffsetX,
        float worldOffsetY,
        PhysicsShadowCommitAction action,
        System.Collections.Immutable.ImmutableArray<uint> crossCellIds)
    {
        if (!_entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? registration))
        {
            return;
        }

        switch (action)
        {
            case PhysicsShadowCommitAction.None:
                RefreshPositionRows(
                    entityId,
                    registration,
                    worldPosition,
                    worldRotation,
                    seedCellId);
                return;
            case PhysicsShadowCommitAction.Recalculate:
                UpdatePosition(
                    entityId,
                    worldPosition,
                    worldRotation,
                    worldOffsetX,
                    worldOffsetY,
                    landblockId: seedCellId & 0xFFFF0000u,
                    seedCellId);
                return;
            case PhysicsShadowCommitAction.Replace:
                if (crossCellIds.IsDefaultOrEmpty)
                {
                    RefreshPositionRows(
                        entityId,
                        registration,
                        worldPosition,
                        worldRotation,
                        seedCellId);
                    return;
                }
                ReplacePositionRows(
                    entityId,
                    registration,
                    worldPosition,
                    worldRotation,
                    seedCellId,
                    crossCellIds);
                return;
            case PhysicsShadowCommitAction.Preserve:
                RefreshPositionRows(
                    entityId,
                    registration,
                    worldPosition,
                    worldRotation,
                    seedCellId);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    internal sealed record PreparedSetPositionShadowCommit(
        ulong CommitId,
        uint EntityId,
        ulong ExpectedMutationRevision,
        ulong ExpectedOwnerVersion,
        ulong FinalMutationRevision,
        ulong FinalOwnerVersion,
        bool ProvenShapeless,
        PreparedShadowOwnerState? OwnerState,
        PreparedShadowCellReplacement[] CellReplacements,
        PreparedShadowPrefixReplacement[] PrefixReplacements,
        HashSet<uint>? OwnerPrefixes,
        uint[] ChangedPrefixes,
        PreparedShadowRetailCellReplacement[] RetailCellReplacements);

    internal sealed record PreparedShadowCellReplacement(
        uint CellId,
        List<ShadowEntry> Entries);

    internal sealed record PreparedShadowRetailCellReplacement(
        uint CellId,
        List<RetailPartEntry> Entries);

    internal sealed record PreparedShadowPrefixReplacement(
        uint Prefix,
        bool Remove,
        List<uint>? Slots,
        Dictionary<uint, int>? Indices,
        Stack<int>? FreeSlots);

    internal readonly record struct SetPositionShadowCommitReceipt(
        ulong CommitId,
        uint EntityId,
        ulong OwnerVersion,
        uint[] ChangedPrefixes,
        bool Mutated)
    {
        internal bool IsValid => CommitId != 0UL && EntityId != 0u;
    }

    internal bool TryPrepareSetPosition(
        uint entityId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        uint seedCellId,
        float worldOffsetX,
        float worldOffsetY,
        PhysicsShadowCommitAction action,
        System.Collections.Immutable.ImmutableArray<uint> crossCellIds,
        bool provenShapeless,
        bool suspendOwner,
        out PreparedSetPositionShadowCommit? prepared)
    {
        prepared = null;
        ulong expectedMutation = _mutationRevision;
        ulong expectedOwner = GetOwnerVersion(entityId);
        bool hasOwner = TryCaptureOwnerState(
            entityId,
            out PreparedShadowOwnerState? source);
        if (!hasOwner)
        {
            if (!provenShapeless)
                return false;
            _pendingSetPositionDispatches.EnsureCapacity(
                _pendingSetPositionDispatches.Count + 1);
            prepared = new PreparedSetPositionShadowCommit(
                checked(++_nextPreparedSetPositionCommitId),
                entityId,
                expectedMutation,
                expectedOwner,
                expectedMutation,
                expectedOwner,
                ProvenShapeless: true,
                OwnerState: null,
                CellReplacements: [],
                PrefixReplacements: [],
                OwnerPrefixes: null,
                ChangedPrefixes: Array.Empty<uint>(),
                RetailCellReplacements: []);
            return _mutationRevision == expectedMutation
                && GetOwnerVersion(entityId) == expectedOwner
                && !HasLogicalOwner(entityId);
        }
        if (provenShapeless || source is null)
            return false;

        var staging = new ShadowObjectRegistry
        {
            DataCache = DataCache,
        };
        staging.InstallOwnerState(source);
        staging.CommitSetPosition(
            entityId,
            worldPosition,
            worldRotation,
            seedCellId,
            worldOffsetX,
            worldOffsetY,
            action,
            crossCellIds);
        if (suspendOwner && !staging.Suspend(entityId))
            return false;
        if (!staging.TryCaptureOwnerState(
                entityId,
                out PreparedShadowOwnerState? replacement)
            || replacement is null)
        {
            return false;
        }

        uint[] changedPrefixes = CaptureChangedPrefixes(source, replacement);
        PreparedShadowCellReplacement[] cellReplacements =
            PrepareCellReplacements(entityId, source, replacement);
        PreparedShadowRetailCellReplacement[] retailCellReplacements =
            PrepareRetailPartEntryReplacements(entityId, source, replacement);
        HashSet<uint> replacementPrefixes = CapturePrefixes(replacement);
        PreparedShadowPrefixReplacement[] prefixReplacements =
            PreparePrefixReplacements(
                entityId,
                CapturePrefixes(source),
                replacementPrefixes,
                changedPrefixes);
        ulong finalMutation = checked(expectedMutation + 1UL);
        ulong finalOwner = checked(expectedOwner + 1UL);
        _cells.EnsureCapacity(_cells.Count + replacement.Rows.Count);
        _entityToCells.EnsureCapacity(_entityToCells.Count + 1);
        _entityReg.EnsureCapacity(_entityReg.Count + 1);
        _entityShapes.EnsureCapacity(_entityShapes.Count + 1);
        _suspendedEntityCells.EnsureCapacity(_suspendedEntityCells.Count + 1);
        _withdrawnPrefixesByOwner.EnsureCapacity(
            _withdrawnPrefixesByOwner.Count + 1);
        _ownerVersions.EnsureCapacity(_ownerVersions.Count + 1);
        _ownerPrefixes.EnsureCapacity(_ownerPrefixes.Count + 1);
        _prefixOwnerSlots.EnsureCapacity(
            _prefixOwnerSlots.Count + changedPrefixes.Length);
        _prefixOwnerIndices.EnsureCapacity(
            _prefixOwnerIndices.Count + changedPrefixes.Length);
        _prefixFreeSlots.EnsureCapacity(
            _prefixFreeSlots.Count + changedPrefixes.Length);
        _suspendedEntities.EnsureCapacity(_suspendedEntities.Count + 1);
        _pendingSetPositionDispatches.EnsureCapacity(
            _pendingSetPositionDispatches.Count + 1);
        _entityRetailPartArrays.EnsureCapacity(_entityRetailPartArrays.Count + 1);
        _retailCellArrays.EnsureCapacity(_retailCellArrays.Count + 1);
        _retailCellArrayRoutes.EnsureCapacity(_retailCellArrayRoutes.Count + 1);
        _retailPartEntriesByCell.EnsureCapacity(
            _retailPartEntriesByCell.Count + retailCellReplacements.Length);

        prepared = new PreparedSetPositionShadowCommit(
            checked(++_nextPreparedSetPositionCommitId),
            entityId,
            expectedMutation,
            expectedOwner,
            finalMutation,
            finalOwner,
            ProvenShapeless: false,
            replacement,
            cellReplacements,
            prefixReplacements,
            replacementPrefixes,
            changedPrefixes,
            retailCellReplacements);
        return _mutationRevision == expectedMutation
            && GetOwnerVersion(entityId) == expectedOwner
            && HasLogicalOwner(entityId);
    }

    internal bool TryApplySetPosition(
        PreparedSetPositionShadowCommit prepared,
        out SetPositionShadowCommitReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        receipt = default;
        if (prepared.CommitId <= _lastAppliedSetPositionCommitId
            || _mutationRevision != prepared.ExpectedMutationRevision
            || GetOwnerVersion(prepared.EntityId)
                != prepared.ExpectedOwnerVersion
            || HasLogicalOwner(prepared.EntityId)
                == prepared.ProvenShapeless)
        {
            return false;
        }

        if (prepared.ProvenShapeless)
        {
            receipt = new SetPositionShadowCommitReceipt(
                prepared.CommitId,
                prepared.EntityId,
                prepared.ExpectedOwnerVersion,
                Array.Empty<uint>(),
                Mutated: false);
            _lastAppliedSetPositionCommitId = prepared.CommitId;
            _pendingSetPositionDispatches.Add(prepared.CommitId);
            return true;
        }
        if (prepared.OwnerState is null)
            return false;

        for (int index = 0; index < prepared.CellReplacements.Length; index++)
        {
            PreparedShadowCellReplacement replacement =
                prepared.CellReplacements[index];
            _cells[replacement.CellId] = replacement.Entries;
        }
        for (int index = 0; index < prepared.RetailCellReplacements.Length; index++)
        {
            PreparedShadowRetailCellReplacement replacement =
                prepared.RetailCellReplacements[index];
            _retailPartEntriesByCell[replacement.CellId] = replacement.Entries;
        }
        PreparedShadowOwnerState state = prepared.OwnerState;
        _entityReg[prepared.EntityId] = state.Registration;
        ReplaceOwnerValue(_entityShapes, prepared.EntityId, state.Shapes);
        if (state.Suspended)
            _suspendedEntities.Add(prepared.EntityId);
        else
            _suspendedEntities.Remove(prepared.EntityId);
        ReplaceOwnerValue(
            _suspendedEntityCells,
            prepared.EntityId,
            state.SuspendedCellIds);
        ReplaceOwnerValue(
            _withdrawnPrefixesByOwner,
            prepared.EntityId,
            state.WithdrawnPrefixes);
        ReplaceOwnerValue(
            _entityToCells,
            prepared.EntityId,
            state.CellIds);
        if (state.RetailPartArray is not null)
        {
            _entityRetailPartArrays[prepared.EntityId] = state.RetailPartArray;
            _retailCellArrayRoutes[prepared.EntityId] = state.RetailRoute;
        }
        else
        {
            _entityRetailPartArrays.Remove(prepared.EntityId);
            _retailCellArrayRoutes.Remove(prepared.EntityId);
        }
        ReplaceOwnerValue(
            _retailCellArrays,
            prepared.EntityId,
            state.RetailCellIds);
        if (prepared.OwnerPrefixes is not null)
            _ownerPrefixes[prepared.EntityId] = prepared.OwnerPrefixes;
        for (int index = 0; index < prepared.PrefixReplacements.Length; index++)
        {
            PreparedShadowPrefixReplacement replacement =
                prepared.PrefixReplacements[index];
            if (replacement.Remove)
            {
                _prefixOwnerSlots.Remove(replacement.Prefix);
                _prefixOwnerIndices.Remove(replacement.Prefix);
                _prefixFreeSlots.Remove(replacement.Prefix);
                continue;
            }
            _prefixOwnerSlots[replacement.Prefix] = replacement.Slots!;
            _prefixOwnerIndices[replacement.Prefix] = replacement.Indices!;
            _prefixFreeSlots[replacement.Prefix] = replacement.FreeSlots!;
        }
        _mutationRevision = prepared.FinalMutationRevision;
        _ownerVersions[prepared.EntityId] = prepared.FinalOwnerVersion;
        _lastAppliedSetPositionCommitId = prepared.CommitId;
        _pendingSetPositionDispatches.Add(prepared.CommitId);
        RepublishAttachedChildren(prepared.EntityId);
        receipt = new SetPositionShadowCommitReceipt(
            prepared.CommitId,
            prepared.EntityId,
            prepared.FinalOwnerVersion,
            prepared.ChangedPrefixes,
            Mutated: true);
        return true;
    }

    internal bool IsPreparedSetPositionCurrent(
        PreparedSetPositionShadowCommit prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return prepared.CommitId > _lastAppliedSetPositionCommitId
            && _mutationRevision == prepared.ExpectedMutationRevision
            && GetOwnerVersion(prepared.EntityId)
                == prepared.ExpectedOwnerVersion
            && HasLogicalOwner(prepared.EntityId)
                != prepared.ProvenShapeless;
    }

    internal void DispatchSetPositionCommit(
        in SetPositionShadowCommitReceipt receipt)
    {
        if (!receipt.IsValid
            || receipt.CommitId > _lastAppliedSetPositionCommitId
            || !_pendingSetPositionDispatches.Remove(receipt.CommitId))
            return;
        if (!receipt.Mutated)
            return;
        ulong currentOwnerVersion = GetOwnerVersion(receipt.EntityId);
        if (!HasLogicalOwner(receipt.EntityId)
            || currentOwnerVersion != receipt.OwnerVersion)
        {
            return;
        }
        for (int index = 0; index < receipt.ChangedPrefixes.Length; index++)
        {
            if (!HasLogicalOwner(receipt.EntityId)
                || GetOwnerVersion(receipt.EntityId) != receipt.OwnerVersion)
            {
                return;
            }
            DispatchSetPositionPrefixObservers(
                receipt.EntityId,
                receipt.ChangedPrefixes[index]);
        }
        if (!HasLogicalOwner(receipt.EntityId))
            return;
        currentOwnerVersion = GetOwnerVersion(receipt.EntityId);
        if (currentOwnerVersion != receipt.OwnerVersion)
            return;
        DispatchSetPositionOwnerObservers(
            receipt.EntityId,
            currentOwnerVersion);
    }

    internal bool DiscardSetPositionCommit(
        in SetPositionShadowCommitReceipt receipt) =>
        receipt.IsValid
        && _pendingSetPositionDispatches.Remove(receipt.CommitId);

    internal int PendingSetPositionDispatchCount =>
        _pendingSetPositionDispatches.Count;

    internal long SetPositionDispatchFailureCount =>
        _setPositionDispatchFailureCount;

    private void DispatchSetPositionPrefixObservers(uint owner, uint prefix)
    {
        Action<uint, uint>? observers = OwnerPrefixMembershipChanged;
        if (observers is null)
            return;
        foreach (Action<uint, uint> observer in observers.GetInvocationList())
        {
            try
            {
                observer(owner, prefix);
            }
            catch
            {
                _setPositionDispatchFailureCount++;
            }
        }
    }

    private void DispatchSetPositionOwnerObservers(uint owner, ulong version)
    {
        Action<uint, ulong>? observers = OwnerMutated;
        if (observers is null)
            return;
        foreach (Action<uint, ulong> observer in observers.GetInvocationList())
        {
            try
            {
                observer(owner, version);
            }
            catch
            {
                _setPositionDispatchFailureCount++;
            }
        }
    }

    private static uint[] CaptureChangedPrefixes(
        PreparedShadowOwnerState before,
        PreparedShadowOwnerState after)
    {
        HashSet<uint> oldPrefixes = CapturePrefixes(before);
        HashSet<uint> newPrefixes = CapturePrefixes(after);
        var changed = new List<uint>();
        foreach (uint prefix in oldPrefixes)
        {
            if (!newPrefixes.Contains(prefix))
                changed.Add(prefix);
        }
        foreach (uint prefix in newPrefixes)
        {
            if (!oldPrefixes.Contains(prefix))
                changed.Add(prefix);
        }
        changed.Sort();
        return changed.ToArray();
    }

    private static HashSet<uint> CapturePrefixes(
        PreparedShadowOwnerState state)
    {
        var prefixes = new HashSet<uint>
        {
            state.Registration.SeedCellId & 0xFFFF0000u,
        };
        if (state.CellIds is not null)
        {
            for (int index = 0; index < state.CellIds.Count; index++)
                prefixes.Add(state.CellIds[index] & 0xFFFF0000u);
        }
        if (state.WithdrawnPrefixes is not null)
        {
            foreach (uint prefix in state.WithdrawnPrefixes)
                prefixes.Add(prefix & 0xFFFF0000u);
        }
        return prefixes;
    }

    private PreparedShadowCellReplacement[] PrepareCellReplacements(
        uint entityId,
        PreparedShadowOwnerState before,
        PreparedShadowOwnerState after)
    {
        var touched = new HashSet<uint>();
        AddCells(touched, before.CellIds);
        AddCells(touched, after.CellIds);
        var afterRows = new Dictionary<uint, ShadowEntry[]>();
        for (int index = 0; index < after.Rows.Count; index++)
        {
            PreparedShadowCellRows row = after.Rows[index];
            touched.Add(row.CellId);
            afterRows[row.CellId] = row.Entries;
        }
        for (int index = 0; index < before.Rows.Count; index++)
            touched.Add(before.Rows[index].CellId);

        uint[] ordered = touched.ToArray();
        Array.Sort(ordered);
        var result = new PreparedShadowCellReplacement[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            uint cellId = ordered[index];
            _cells.TryGetValue(cellId, out List<ShadowEntry>? active);
            afterRows.TryGetValue(cellId, out ShadowEntry[]? ownerRows);
            int retainedCount = 0;
            if (active is not null)
            {
                for (int row = 0; row < active.Count; row++)
                {
                    if (active[row].EntityId != entityId)
                        retainedCount++;
                }
            }
            var replacement = new List<ShadowEntry>(
                retainedCount + (ownerRows?.Length ?? 0));
            if (active is not null)
            {
                for (int row = 0; row < active.Count; row++)
                {
                    if (active[row].EntityId != entityId)
                        replacement.Add(active[row]);
                }
            }
            if (ownerRows is not null)
                replacement.AddRange(ownerRows);
            result[index] = new PreparedShadowCellReplacement(
                cellId,
                replacement);
        }
        return result;
    }

    private PreparedShadowRetailCellReplacement[] PrepareRetailPartEntryReplacements(
        uint entityId,
        PreparedShadowOwnerState before,
        PreparedShadowOwnerState after)
    {
        var touched = new HashSet<uint>();
        AddCells(touched, before.RetailCellIds);
        AddCells(touched, after.RetailCellIds);
        var afterRows = new Dictionary<uint, RetailPartEntry[]>();
        for (int index = 0; index < after.RetailRows.Count; index++)
        {
            PreparedShadowRetailPartRows row = after.RetailRows[index];
            touched.Add(row.CellId);
            afterRows[row.CellId] = row.Entries;
        }
        for (int index = 0; index < before.RetailRows.Count; index++)
            touched.Add(before.RetailRows[index].CellId);

        uint[] ordered = touched.ToArray();
        Array.Sort(ordered);
        var result = new PreparedShadowRetailCellReplacement[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            uint cellId = ordered[index];
            _retailPartEntriesByCell.TryGetValue(cellId, out List<RetailPartEntry>? active);
            afterRows.TryGetValue(cellId, out RetailPartEntry[]? ownerRows);
            int retainedCount = 0;
            if (active is not null)
            {
                for (int row = 0; row < active.Count; row++)
                {
                    if (active[row].EntityId != entityId)
                        retainedCount++;
                }
            }
            var replacement = new List<RetailPartEntry>(
                retainedCount + (ownerRows?.Length ?? 0));
            if (active is not null)
            {
                for (int row = 0; row < active.Count; row++)
                {
                    if (active[row].EntityId != entityId)
                        replacement.Add(active[row]);
                }
            }
            if (ownerRows is not null)
                replacement.AddRange(ownerRows);
            result[index] = new PreparedShadowRetailCellReplacement(
                cellId,
                replacement);
        }
        return result;
    }

    private PreparedShadowPrefixReplacement[] PreparePrefixReplacements(
        uint entityId,
        HashSet<uint> before,
        HashSet<uint> after,
        uint[] changedPrefixes)
    {
        var result = new PreparedShadowPrefixReplacement[
            changedPrefixes.Length];
        for (int index = 0; index < changedPrefixes.Length; index++)
        {
            uint prefix = changedPrefixes[index];
            bool removeOwner = before.Contains(prefix)
                && !after.Contains(prefix);
            _prefixOwnerSlots.TryGetValue(prefix, out List<uint>? oldSlots);
            _prefixOwnerIndices.TryGetValue(
                prefix,
                out Dictionary<uint, int>? oldIndices);
            _prefixFreeSlots.TryGetValue(prefix, out Stack<int>? oldFree);
            var slots = oldSlots is null ? [] : new List<uint>(oldSlots);
            var indices = oldIndices is null
                ? new Dictionary<uint, int>()
                : new Dictionary<uint, int>(oldIndices);
            Stack<int> free = CloneStack(oldFree);
            if (removeOwner)
            {
                if (indices.Remove(entityId, out int ownerSlot))
                {
                    slots[ownerSlot] = 0u;
                    free.Push(ownerSlot);
                }
                result[index] = indices.Count == 0
                    ? new PreparedShadowPrefixReplacement(
                        prefix,
                        Remove: true,
                        Slots: null,
                        Indices: null,
                        FreeSlots: null)
                    : new PreparedShadowPrefixReplacement(
                        prefix,
                        Remove: false,
                        slots,
                        indices,
                        free);
                continue;
            }

            if (!indices.ContainsKey(entityId))
            {
                if (free.TryPop(out int freeIndex))
                {
                    slots[freeIndex] = entityId;
                    indices[entityId] = freeIndex;
                }
                else
                {
                    indices[entityId] = slots.Count;
                    slots.Add(entityId);
                }
            }
            result[index] = new PreparedShadowPrefixReplacement(
                prefix,
                Remove: false,
                slots,
                indices,
                free);
        }
        return result;
    }

    private static Stack<int> CloneStack(Stack<int>? source) =>
        source is null
            ? new Stack<int>()
            : new Stack<int>(source.Reverse());

    private static void AddCells(HashSet<uint> destination, List<uint>? cells)
    {
        if (cells is null)
            return;
        for (int index = 0; index < cells.Count; index++)
            destination.Add(cells[index]);
    }

    private static void ReplaceOwnerValue<T>(
        Dictionary<uint, T> destination,
        uint entityId,
        T? value)
        where T : class
    {
        if (value is null)
            destination.Remove(entityId);
        else
            destination[entityId] = value;
    }

    private void RefreshPositionRows(
        uint entityId,
        RegistrationRecord registration,
        Vector3 worldPosition,
        Quaternion worldRotation,
        uint seedCellId)
    {
        if ((_entityToCells.TryGetValue(
                 entityId,
                 out List<uint>? retainedCells)
             || _suspendedEntityCells.TryGetValue(
                 entityId,
                 out retainedCells))
            && retainedCells.Count != 0)
        {
            ReplacePositionRows(
                entityId,
                registration,
                worldPosition,
                worldRotation,
                seedCellId,
                retainedCells);
            return;
        }

        if (!_entityToCells.ContainsKey(entityId)
            && !_suspendedEntityCells.ContainsKey(entityId)
            && seedCellId != 0u
            && _entityRetailPartArrays.TryGetValue(
                entityId,
                out IReadOnlyList<ShadowShape>? renderPartArray)
            && renderPartArray.Count != 0)
        {
            _suspendedEntities.Remove(entityId);
            _entityReg[entityId] = registration with
            {
                SeedCellId = seedCellId,
                EntityWorldPos = worldPosition,
                EntityWorldRot = worldRotation,
            };
            _singleCellScratch[0] = seedCellId;
            PublishRetailProductFromExactCells(entityId, _singleCellScratch);
            BumpOwnerVersion(entityId);
            return;
        }

        _entityReg[entityId] = registration with
        {
            SeedCellId = seedCellId,
            EntityWorldPos = worldPosition,
            EntityWorldRot = worldRotation,
        };
        BumpOwnerVersion(entityId);
    }

    private readonly uint[] _singleCellScratch = new uint[1];

    private void ReplacePositionRows(
        uint entityId,
        RegistrationRecord registration,
        Vector3 worldPosition,
        Quaternion worldRotation,
        uint seedCellId,
        IReadOnlyList<uint> cellIds)
    {
        if (_entityToCells.TryGetValue(
                entityId,
                out List<uint>? previousCells))
        {
            for (int index = 0; index < previousCells.Count; index++)
            {
                if (_cells.TryGetValue(
                        previousCells[index],
                        out List<ShadowEntry>? entries))
                {
                    RemoveOwnerRows(entries, entityId);
                }
            }
        }

        _suspendedEntities.Remove(entityId);
        _suspendedEntityCells.Remove(entityId);
        _entityReg[entityId] = registration with
        {
            SeedCellId = seedCellId,
            EntityWorldPos = worldPosition,
            EntityWorldRot = worldRotation,
        };

        var exactCells = new List<uint>(cellIds.Count);
        for (int index = 0; index < cellIds.Count; index++)
        {
            uint cellId = cellIds[index];
            if (cellId == 0u || exactCells.Contains(cellId))
                continue;
            exactCells.Add(cellId);
        }

        IReadOnlyList<ShadowShape>? shapes = null;
        bool isMultiPartDispatch = registration.IsMultiPart
            && _entityShapes.TryGetValue(entityId, out shapes);
        if (isMultiPartDispatch)
        {
            foreach (ShadowShape shape in shapes!)
            {
                Vector3 partWorldPosition = worldPosition
                    + Vector3.Transform(shape.LocalPosition, worldRotation);
                Quaternion partWorldRotation = worldRotation
                    * shape.LocalRotation;
                var entry = new ShadowEntry(
                    entityId,
                    shape.GfxObjId,
                    partWorldPosition,
                    partWorldRotation,
                    shape.Radius,
                    shape.CollisionType,
                    shape.CylHeight,
                    shape.Scale,
                    registration.State,
                    registration.Flags,
                    shape.LocalPosition,
                    shape.LocalRotation);
                for (int index = 0; index < exactCells.Count; index++)
                    AddEntryToCell(entry, exactCells[index]);
            }
        }
        else
        {
            var entry = new ShadowEntry(
                entityId,
                registration.GfxObjId,
                worldPosition,
                worldRotation,
                registration.Radius,
                registration.CollisionType,
                registration.CylHeight,
                registration.Scale,
                registration.State,
                registration.Flags);
            for (int index = 0; index < exactCells.Count; index++)
                AddEntryToCell(entry, exactCells[index]);
        }

        bool wroteCollisionEntries = !isMultiPartDispatch || shapes!.Count != 0;
        if (exactCells.Count == 0 || !wroteCollisionEntries)
            _entityToCells.Remove(entityId);
        else
            _entityToCells[entityId] = exactCells;
        if (_withdrawnPrefixesByOwner.TryGetValue(
                entityId,
                out HashSet<uint>? withdrawn))
        {
            for (int index = 0; index < exactCells.Count; index++)
                withdrawn.Remove(exactCells[index] & 0xFFFF0000u);
            if (withdrawn.Count == 0)
                _withdrawnPrefixesByOwner.Remove(entityId);
        }
        PublishRetailProductFromExactCells(entityId, exactCells);
        BumpOwnerVersion(entityId);
    }

    public bool Suspend(uint entityId)
    {
        if (!_entityReg.ContainsKey(entityId))
            return false;

        if (_entityToCells.TryGetValue(entityId, out var cellIds))
        {
            _suspendedEntityCells[entityId] = new List<uint>(cellIds);
            foreach (uint cellId in cellIds)
            {
                if (_cells.TryGetValue(cellId, out var list))
                    RemoveOwnerRows(list, entityId);
            }
            _entityToCells.Remove(entityId);
        }

        if (_retailCellArrays.TryGetValue(entityId, out List<uint>? retailCells))
        {
            RemoveRetailPartEntriesFromCells(entityId, retailCells);
            _retailCellArrays.Remove(entityId);
            RepublishAttachedChildren(entityId);
        }

        _suspendedEntities.Add(entityId);
        BumpOwnerVersion(entityId);
        return true;
    }

    public void RefloodLandblock(uint landblockId)
    {
        uint[] owners = CaptureRefloodOwnersForLandblock(landblockId);
        for (int i = 0; i < owners.Length; i++)
            RefloodOwnerForLandblock(owners[i], landblockId);
    }

    public uint[] CaptureRefloodOwnersForLandblock(uint landblockId)
    {
        uint lbPrefix = landblockId & 0xFFFF0000u;
        var toReflood = new HashSet<uint>();

        foreach (var kvp in _entityReg)
        {
            if (_suspendedEntities.Contains(kvp.Key))
                continue;

            if ((kvp.Value.SeedCellId & 0xFFFF0000u) == lbPrefix)
            {
                toReflood.Add(kvp.Key);
                continue;
            }
            if (_entityToCells.TryGetValue(kvp.Key, out var cells))
            {
                foreach (uint c in cells)
                {
                    if ((c & 0xFFFF0000u) == lbPrefix)
                    {
                        toReflood.Add(kvp.Key);
                        break;
                    }
                }
            }
            if (_withdrawnPrefixesByOwner.TryGetValue(kvp.Key, out var prefixes)
                && prefixes.Contains(lbPrefix))
            {
                toReflood.Add(kvp.Key);
            }
        }

        uint[] ordered = toReflood.ToArray();
        Array.Sort(ordered);
        return ordered;
    }

    /// <summary>
    /// Re-runs one owner from a retained landblock-reflood receipt. A removed,
    /// suspended, or otherwise superseded owner is an idempotent no-op.
    /// </summary>
    public void RefloodOwnerForLandblock(uint entityId, uint landblockId)
    {
        uint lbPrefix = landblockId & 0xFFFF0000u;
        if (_suspendedEntities.Contains(entityId)
            || !_entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? reg))
        {
            return;
        }

        _withdrawnPrefixesByOwner.TryGetValue(
            entityId,
            out var withdrawnBeforeReflood);

        _entityRetailPartArrays.TryGetValue(
            entityId,
            out IReadOnlyList<ShadowShape>? retainedPartArray);

        if (reg.IsMultiPart
            && _entityShapes.TryGetValue(entityId, out var shapes))
        {
            RegisterMultiPart(
                entityId,
                reg.EntityWorldPos,
                reg.EntityWorldRot,
                shapes,
                reg.State,
                reg.Flags,
                0f,
                0f,
                lbPrefix,
                reg.SeedCellId,
                reg.IsStatic,
                publishMutation: false,
                partArray: retainedPartArray);
        }
        else
        {
            Register(
                entityId,
                reg.GfxObjId,
                reg.EntityWorldPos,
                reg.EntityWorldRot,
                reg.Radius,
                0f,
                0f,
                lbPrefix,
                reg.CollisionType,
                reg.CylHeight,
                reg.Scale,
                reg.State,
                reg.Flags,
                reg.SeedCellId,
                reg.IsStatic,
                publishMutation: false,
                partArray: retainedPartArray);
        }

        if (withdrawnBeforeReflood is not null)
            _withdrawnPrefixesByOwner[entityId] = withdrawnBeforeReflood;

        if (_entityToCells.TryGetValue(entityId, out var refreshedCells)
            && refreshedCells.Exists(cell =>
                (cell & 0xFFFF0000u) == lbPrefix)
            && _withdrawnPrefixesByOwner.TryGetValue(
                entityId,
                out var withdrawn))
        {
            withdrawn.Remove(lbPrefix);
            if (withdrawn.Count == 0)
                _withdrawnPrefixesByOwner.Remove(entityId);
        }
        BumpOwnerVersion(entityId);
    }

    public void UpdatePhysicsState(uint entityId, uint newState)
    {
        bool retained = _entityReg.TryGetValue(
            entityId,
            out RegistrationRecord? retainedRegistration);
        if (retained)
        {
            _entityReg[entityId] = retainedRegistration! with { State = newState };
        }

        if (!_entityToCells.TryGetValue(entityId, out var cellIds))
        {
            if (retained)
                BumpOwnerVersion(entityId);
            return; // not registered — no-op

        }

        foreach (var cellId in cellIds)
        {
            if (!_cells.TryGetValue(cellId, out var list)) continue;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityId == entityId)
                    list[i] = list[i] with { State = newState };
            }
        }

        if (retained)
            BumpOwnerVersion(entityId);

    }

    public void UpdatePwdBitfieldFlags(uint entityId, uint pwdBitfield)
    {
        EntityCollisionFlags decoded = EntityCollisionFlagsExt.FromPwdBitfield(pwdBitfield);

        bool retained = _entityReg.TryGetValue(
            entityId,
            out RegistrationRecord? retainedRegistration);
        if (retained)
        {
            EntityCollisionFlags merged =
                (retainedRegistration!.Flags & ~EntityCollisionFlagsExt.PwdBitfieldDerivedMask)
                | decoded;
            if (merged == retainedRegistration.Flags)
                return;
            _entityReg[entityId] = retainedRegistration with { Flags = merged };
        }

        if (!_entityToCells.TryGetValue(entityId, out var cellIds))
        {
            if (retained)
                BumpOwnerVersion(entityId);
            return; // not registered — no-op
        }

        foreach (var cellId in cellIds)
        {
            if (!_cells.TryGetValue(cellId, out var list)) continue;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].EntityId == entityId)
                {
                    EntityCollisionFlags merged =
                        (list[i].Flags & ~EntityCollisionFlagsExt.PwdBitfieldDerivedMask)
                        | decoded;
                    list[i] = list[i] with { Flags = merged };
                }
            }
        }

        if (retained)
            BumpOwnerVersion(entityId);
    }

    public void Deregister(uint entityId)
    {
        if (_parentChildren.TryGetValue(entityId, out List<uint>? children)
            && children.Count > 0)
        {
            uint[] toDetach = children.ToArray();
            for (int i = 0; i < toDetach.Length; i++)
                DetachChildCore(toDetach[i], removeFromParentList: false);
            _parentChildren.Remove(entityId);
        }
        DetachChildCore(entityId, removeFromParentList: true);
        DeregisterCore(entityId, publishMutation: true);
    }

    private void DeregisterCore(uint entityId, bool publishMutation)
    {
        bool existed = _entityReg.ContainsKey(entityId)
            || _entityToCells.ContainsKey(entityId)
            || _entityShapes.ContainsKey(entityId)
            || _suspendedEntities.Contains(entityId)
            || _suspendedEntityCells.ContainsKey(entityId);
        if (_entityToCells.TryGetValue(entityId, out var cellIds))
        {
            foreach (var cellId in cellIds)
            {
                if (_cells.TryGetValue(cellId, out var list))
                    RemoveOwnerRows(list, entityId);
            }
            _entityToCells.Remove(entityId);
        }
        _entityShapes.Remove(entityId);
        _entityReg.Remove(entityId);
        _suspendedEntities.Remove(entityId);
        _suspendedEntityCells.Remove(entityId);
        _withdrawnPrefixesByOwner.Remove(entityId);
        ClearRetailCellArray(entityId);
        if (existed && publishMutation)
        {
            BumpOwnerVersion(entityId);
            RemoveOwnerPrefixMembership(entityId);
            _ownerVersions.Remove(entityId);
        }
    }

    private static void RemoveOwnerRows(
        List<ShadowEntry> entries,
        uint entityId)
    {
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index].EntityId == entityId)
                entries.RemoveAt(index);
        }
    }

    public void DeregisterStaticOwnersForLandblock(uint landblockId)
    {
        uint[] owners = CaptureStaticOwnersForLandblock(landblockId);
        for (int i = 0; i < owners.Length; i++)
            DeregisterStaticOwnerForLandblock(owners[i], landblockId);
    }

    public uint[] CaptureStaticOwnersForLandblock(uint landblockId)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        var owners = new List<uint>();
        foreach (var (entityId, registration) in _entityReg)
        {
            if (registration.IsStatic
                && (registration.SeedCellId & 0xFFFF0000u) == prefix)
            {
                owners.Add(entityId);
            }
        }
        owners.Sort();
        return owners.ToArray();
    }

    /// <summary>
    /// Removes one static owner from a retained landblock receipt. If the
    /// owner was already removed or rebound elsewhere, the operation no-ops.
    /// </summary>
    public void DeregisterStaticOwnerForLandblock(
        uint entityId,
        uint landblockId)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        if (_entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? registration)
            && registration.IsStatic
            && (registration.SeedCellId & 0xFFFF0000u) == prefix)
        {
            Deregister(entityId);
        }
    }

    public void RemoveLandblock(uint landblockId)
    {
        uint lbPrefix = landblockId & 0xFFFF0000u;
        var toRemove = new List<uint>();
        var touchedOwners = new HashSet<uint>();

        foreach (var (entityId, cells) in _entityToCells)
        {
            if (!cells.Exists(cell => (cell & 0xFFFF0000u) == lbPrefix))
                continue;
            touchedOwners.Add(entityId);
            if (!_withdrawnPrefixesByOwner.TryGetValue(entityId, out var withdrawn))
            {
                withdrawn = new HashSet<uint>();
                _withdrawnPrefixesByOwner[entityId] = withdrawn;
            }
            withdrawn.Add(lbPrefix);
        }

        foreach (var kvp in _cells)
        {
            if ((kvp.Key & 0xFFFF0000u) == lbPrefix)
                toRemove.Add(kvp.Key);
        }

        foreach (var cellId in toRemove)
            _cells.Remove(cellId);

        var entitiesToRemove = new List<uint>();
        foreach (var kvp in _entityToCells)
        {
            kvp.Value.RemoveAll(c => (c & 0xFFFF0000u) == lbPrefix);
            if (kvp.Value.Count == 0)
                entitiesToRemove.Add(kvp.Key);
        }
        foreach (var eid in entitiesToRemove)
        {
            _entityToCells.Remove(eid);
            // A streamed-out server-live object is still logically alive.
            // Preserve dynamic registration/shape payload for the reload
            // reflood; static owners end with their landblock.
            if (!_entityReg.TryGetValue(eid, out var registration)
                || registration.IsStatic)
            {
                _entityShapes.Remove(eid);
                _entityReg.Remove(eid);
                _suspendedEntities.Remove(eid);
                _suspendedEntityCells.Remove(eid);
                _withdrawnPrefixesByOwner.Remove(eid);
            }
        }
        RemoveRetailProductForPrefix(lbPrefix, touchedOwners);
        foreach (uint entityId in touchedOwners)
            BumpOwnerVersion(entityId);
    }

    private readonly List<uint> _prefixRemovalScratch = new();

    private void RemoveRetailProductForPrefix(
        uint lbPrefix,
        HashSet<uint> touchedOwners)
    {
        _prefixRemovalScratch.Clear();
        foreach (uint cellId in _retailPartEntriesByCell.Keys)
        {
            if ((cellId & 0xFFFF0000u) == lbPrefix)
                _prefixRemovalScratch.Add(cellId);
        }
        for (int i = 0; i < _prefixRemovalScratch.Count; i++)
            _retailPartEntriesByCell.Remove(_prefixRemovalScratch[i]);

        _prefixRemovalScratch.Clear();
        foreach (var (ownerId, cells) in _retailCellArrays)
        {
            for (int i = cells.Count - 1; i >= 0; i--)
            {
                if ((cells[i] & 0xFFFF0000u) == lbPrefix)
                {
                    cells.RemoveAt(i);
                    touchedOwners.Add(ownerId);
                }
            }
            if (cells.Count == 0)
                _prefixRemovalScratch.Add(ownerId);
        }
        for (int i = 0; i < _prefixRemovalScratch.Count; i++)
        {
            uint ownerId = _prefixRemovalScratch[i];
            _retailCellArrays.Remove(ownerId);
            bool endsWithLandblock =
                !_entityReg.TryGetValue(ownerId, out RegistrationRecord? registration)
                || registration.IsStatic;
            if (!endsWithLandblock)
                continue;
            _retailCellArrayRoutes.Remove(ownerId);
            _entityRetailPartArrays.Remove(ownerId);
            _entityShapes.Remove(ownerId);
            _entityReg.Remove(ownerId);
            _suspendedEntities.Remove(ownerId);
            _suspendedEntityCells.Remove(ownerId);
            _withdrawnPrefixesByOwner.Remove(ownerId);
        }
    }

    internal void RetireOwnerFromLandblock(uint entityId, uint landblockId)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        if (_entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? registration)
            && registration.IsStatic
            && (registration.SeedCellId & 0xFFFF0000u) == prefix)
        {
            DeregisterCore(entityId, publishMutation: false);
            RemoveOwnerPrefixMembership(entityId);
            _ownerVersions.Remove(entityId);
            AdvanceMutationRevision();
            return;
        }
        bool touched = false;
        if (_retailCellArrays.TryGetValue(entityId, out List<uint>? retailCells))
        {
            for (int index = retailCells.Count - 1; index >= 0; index--)
            {
                uint cellId = retailCells[index];
                if ((cellId & 0xFFFF0000u) != prefix)
                    continue;
                touched = true;
                retailCells.RemoveAt(index);
                if (_retailPartEntriesByCell.TryGetValue(
                        cellId,
                        out List<RetailPartEntry>? partRows))
                {
                    RemoveOwnerPartRows(partRows, entityId);
                    if (partRows.Count == 0)
                        _retailPartEntriesByCell.Remove(cellId);
                }
            }
            if (retailCells.Count == 0)
                _retailCellArrays.Remove(entityId);
        }
        if (!_entityToCells.TryGetValue(entityId, out List<uint>? cells))
        {
            if (touched)
                BumpOwnerVersion(entityId);
            return;
        }

        for (int index = cells.Count - 1; index >= 0; index--)
        {
            uint cellId = cells[index];
            if ((cellId & 0xFFFF0000u) != prefix)
                continue;
            touched = true;
            cells.RemoveAt(index);
            if (_cells.TryGetValue(cellId, out List<ShadowEntry>? entries))
            {
                RemoveOwnerRows(entries, entityId);
                if (entries.Count == 0)
                    _cells.Remove(cellId);
            }
        }
        if (!touched)
            return;
        if (!_withdrawnPrefixesByOwner.TryGetValue(
                entityId,
                out HashSet<uint>? withdrawn))
        {
            withdrawn = new HashSet<uint>();
            _withdrawnPrefixesByOwner[entityId] = withdrawn;
        }
        withdrawn.Add(prefix);
        if (cells.Count == 0)
            _entityToCells.Remove(entityId);
        BumpOwnerVersion(entityId);
    }

    public IReadOnlyList<ShadowEntry> GetObjectsInCell(uint cellId)
    {
        if (_cells.TryGetValue(cellId, out var list))
            return list;
        return System.Array.Empty<ShadowEntry>();
    }

    public IReadOnlyList<uint> GetOwnerCells(uint entityId)
    {
        if (_entityToCells.TryGetValue(entityId, out List<uint>? cells))
            return cells;
        return System.Array.Empty<uint>();
    }

    public int TotalRegistered => _entityToCells.Count;

    public int RetainedRegistrationCount => _entityReg.Count;

    /// <summary>Number of owner/prefix repair markers awaiting a future reflood.</summary>
    public int WithdrawnPrefixMarkerCount
    {
        get
        {
            int count = 0;
            foreach (var prefixes in _withdrawnPrefixesByOwner.Values)
                count += prefixes.Count;
            return count;
        }
    }

    /// <summary>Suspended logical registrations awaiting spatial re-entry.</summary>
    public int SuspendedRegistrationCount => _suspendedEntities.Count;

    public bool HasOwnerRowsInLandblock(uint ownerId, uint landblockId) =>
        _entityToCells.TryGetValue(ownerId, out List<uint>? cells)
        && cells.Exists(cell =>
            (cell & 0xFFFF0000u) == (landblockId & 0xFFFF0000u));

    /// <summary>
    /// Mirrors one ordinary active-world mutation into an off-side generation.
    /// Target-prefix owners may subsequently be reflooded against the staged
    /// topology; unrelated owners retain these exact active rows.
    /// </summary>
    internal void MirrorOwnerFrom(
        ShadowObjectRegistry source,
        uint entityId)
    {
        ArgumentNullException.ThrowIfNull(source);
        DeregisterCore(entityId, publishMutation: false);
        if (source.TryCaptureOwnerState(
                entityId,
                out PreparedShadowOwnerState? state)
            && state is not null)
        {
            InstallOwnerState(state);
            _ownerVersions[entityId] = source.GetOwnerVersion(entityId);
        }
        else
        {
            RemoveOwnerPrefixMembership(entityId);
            _ownerVersions.Remove(entityId);
        }
    }

    internal int CaptureOwnerSlotLimit() => _ownerSlots.Count;

    internal uint GetOwnerSlot(int index) => _ownerSlots[index];

    internal void ApplyCommittedOwnerReplacement(
        ShadowObjectRegistry stagingSource,
        uint ownerId,
        uint landblockId)
    {
        ArgumentNullException.ThrowIfNull(stagingSource);
        if (stagingSource.HasLogicalOwner(ownerId))
        {
            MirrorOwnerFrom(stagingSource, ownerId);
            RefloodOwnerForLandblock(ownerId, landblockId);
            return;
        }
        if (IsStaticOwnerRootedIn(ownerId, landblockId))
        {
            // Outgoing generation's authored static, not re-authored by the
            // replacement: it ends with its landblock (same lifetime rule as
            // RetireOwnerFromLandblock's static branch).
            DeregisterCore(ownerId, publishMutation: false);
            RemoveOwnerPrefixMembership(ownerId);
            _ownerVersions.Remove(ownerId);
            AdvanceMutationRevision();
            return;
        }
        RefloodOwnerForLandblock(ownerId, landblockId);
    }

    internal void RefloodPrefixOwnersAfterReplacement(
        uint landblockId,
        IReadOnlyList<uint> sealedOwnerIds)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        if (!_prefixOwnerSlots.TryGetValue(prefix, out List<uint>? slots))
            return;
        var applied = new HashSet<uint>(sealedOwnerIds);
        int limit = slots.Count;
        for (int index = 0; index < limit; index++)
        {
            uint ownerId = slots[index];
            if (ownerId == 0u || !applied.Add(ownerId))
                continue;
            RefloodOwnerForLandblock(ownerId, landblockId);
        }
    }

    internal bool RefreshRetainedOwnerFrom(
        ShadowObjectRegistry source,
        uint entityId,
        uint landblockId,
        out ulong sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        sourceVersion = source.GetOwnerVersion(entityId);
        if (!source._entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? registration)
            || source._suspendedEntities.Contains(entityId)
            || !source.OwnerTouchesLandblock(entityId, landblockId))
        {
            // A target-local refresh is not a global owner deletion. Preserve
            // the exact active rows when the live owner has moved elsewhere.
            MirrorOwnerFrom(source, entityId);
            return false;
        }
        if (registration.IsStatic
            && (registration.SeedCellId & 0xFFFF0000u)
                == (landblockId & 0xFFFF0000u))
        {
            return false;
        }

        DeregisterCore(entityId, publishMutation: false);

        if (registration.IsMultiPart
            && source._entityShapes.TryGetValue(
                entityId,
                out IReadOnlyList<ShadowShape>? shapes))
        {
            RegisterMultiPart(
                entityId,
                registration.EntityWorldPos,
                registration.EntityWorldRot,
                shapes,
                registration.State,
                registration.Flags,
                0f,
                0f,
                landblockId,
                registration.SeedCellId,
                isStatic: registration.IsStatic,
                publishMutation: false);
        }
        else
        {
            Register(
                entityId,
                registration.GfxObjId,
                registration.EntityWorldPos,
                registration.EntityWorldRot,
                registration.Radius,
                0f,
                0f,
                landblockId,
                registration.CollisionType,
                registration.CylHeight,
                registration.Scale,
                registration.State,
                registration.Flags,
                registration.SeedCellId,
                isStatic: registration.IsStatic,
                publishMutation: false);
        }

        if (source._withdrawnPrefixesByOwner.TryGetValue(
                entityId,
                out HashSet<uint>? sourceWithdrawn))
        {
            var retainedWithdrawn = new HashSet<uint>(sourceWithdrawn);
            uint prefix = landblockId & 0xFFFF0000u;
            if (_entityToCells.TryGetValue(entityId, out List<uint>? cells)
                && cells.Exists(cell => (cell & 0xFFFF0000u) == prefix))
            {
                retainedWithdrawn.Remove(prefix);
            }
            if (retainedWithdrawn.Count != 0)
                _withdrawnPrefixesByOwner[entityId] = retainedWithdrawn;
        }
        RefreshOwnerPrefixIndex(entityId);
        _ownerVersions[entityId] = sourceVersion;
        return true;
    }

    internal LandblockReplacementBuilder CreateLandblockReplacementBuilder(
        ShadowObjectRegistry staging,
        uint landblockId,
        IReadOnlyList<uint> expectedRetainedOwners) => new(
            this,
            staging,
            landblockId,
            expectedRetainedOwners);

    internal bool OwnerTouchesLandblock(uint entityId, uint landblockId)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        if (!_entityReg.TryGetValue(entityId, out RegistrationRecord? record))
            return false;
        if ((record.SeedCellId & 0xFFFF0000u) == prefix)
            return true;
        if (_entityToCells.TryGetValue(entityId, out List<uint>? cells)
            && cells.Exists(cell => (cell & 0xFFFF0000u) == prefix))
        {
            return true;
        }
        return _withdrawnPrefixesByOwner.TryGetValue(
                entityId,
                out HashSet<uint>? withdrawn)
            && withdrawn.Contains(prefix);
    }

    internal bool IsStaticOwnerRootedIn(uint entityId, uint landblockId) =>
        _entityReg.TryGetValue(entityId, out RegistrationRecord? registration)
        && registration.IsStatic
        && (registration.SeedCellId & 0xFFFF0000u)
            == (landblockId & 0xFFFF0000u);

    internal bool TryGetStaticOwnerRootPrefix(
        uint entityId,
        out uint landblockPrefix)
    {
        if (_entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? registration)
            && registration.IsStatic)
        {
            landblockPrefix = registration.SeedCellId & 0xFFFF0000u;
            return true;
        }
        landblockPrefix = 0u;
        return false;
    }

    public bool HasLogicalOwner(uint entityId) =>
        _entityReg.ContainsKey(entityId);

    internal bool TryGetCollisionOwner(
        uint entityId,
        out uint physicsState,
        out bool isStatic)
    {
        if (_entityReg.TryGetValue(
                entityId,
                out RegistrationRecord? registration))
        {
            physicsState = registration.State;
            isStatic = registration.IsStatic;
            return true;
        }

        physicsState = 0u;
        isStatic = false;
        return false;
    }

    public int PrefixOwnerSlotCapacityForDiagnostics(uint landblockId) =>
        _prefixOwnerSlots.TryGetValue(
            landblockId & 0xFFFF0000u,
            out List<uint>? slots)
                ? slots.Count
                : 0;

    public int OwnerVersionCountForDiagnostics => _ownerVersions.Count;

    public int PrefixOwnerContainerCountForDiagnostics =>
        _prefixOwnerSlots.Count;

    private bool TryCaptureOwnerState(
        uint entityId,
        out PreparedShadowOwnerState? state)
    {
        if (!_entityReg.TryGetValue(entityId, out RegistrationRecord? registration))
        {
            state = null;
            return false;
        }
        _entityToCells.TryGetValue(entityId, out List<uint>? cells);
        _suspendedEntityCells.TryGetValue(
            entityId,
            out List<uint>? suspendedCells);
        _entityShapes.TryGetValue(
            entityId,
            out IReadOnlyList<ShadowShape>? shapes);
        _withdrawnPrefixesByOwner.TryGetValue(
            entityId,
            out HashSet<uint>? withdrawn);
        var rows = new List<PreparedShadowCellRows>();
        if (cells is not null)
        {
            foreach (uint cellId in cells)
            {
                if (_cells.TryGetValue(cellId, out List<ShadowEntry>? entries))
                {
                    rows.Add(new PreparedShadowCellRows(
                        cellId,
                        CollectOwnerRows(entries, entityId)));
                }
            }
        }

        _entityRetailPartArrays.TryGetValue(
            entityId,
            out IReadOnlyList<ShadowShape>? retailPartArray);
        _retailCellArrays.TryGetValue(entityId, out List<uint>? retailCells);
        RetailCellArrayRoute retailRoute = _retailCellArrayRoutes.TryGetValue(
            entityId,
            out RetailCellArrayRoute capturedRoute)
                ? capturedRoute
                : RetailCellArrayRoute.None;
        var retailRows = new List<PreparedShadowRetailPartRows>();
        if (retailCells is not null)
        {
            foreach (uint cellId in retailCells)
            {
                if (_retailPartEntriesByCell.TryGetValue(
                        cellId,
                        out List<RetailPartEntry>? entries))
                {
                    retailRows.Add(new PreparedShadowRetailPartRows(
                        cellId,
                        CollectOwnerPartRows(entries, entityId)));
                }
            }
        }

        state = new PreparedShadowOwnerState(
            entityId,
            registration,
            shapes,
            cells is null ? null : new List<uint>(cells),
            rows,
            _suspendedEntities.Contains(entityId),
            suspendedCells is null ? null : new List<uint>(suspendedCells),
            withdrawn is null ? null : new HashSet<uint>(withdrawn),
            retailPartArray,
            retailCells is null ? null : new List<uint>(retailCells),
            retailRoute,
            retailRows);
        return true;
    }

    private void InstallOwnerState(PreparedShadowOwnerState state)
    {
        _entityReg[state.EntityId] = state.Registration;
        if (state.Shapes is not null)
            _entityShapes[state.EntityId] = state.Shapes;
        if (state.Suspended)
            _suspendedEntities.Add(state.EntityId);
        if (state.SuspendedCellIds is not null)
            _suspendedEntityCells[state.EntityId] = state.SuspendedCellIds;
        if (state.WithdrawnPrefixes is not null)
        {
            _withdrawnPrefixesByOwner[state.EntityId] = state.WithdrawnPrefixes;
        }
        if (state.CellIds is not null)
            _entityToCells[state.EntityId] = state.CellIds;
        for (int rowIndex = 0; rowIndex < state.Rows.Count; rowIndex++)
        {
            PreparedShadowCellRows row = state.Rows[rowIndex];
            for (int entryIndex = 0; entryIndex < row.Entries.Length; entryIndex++)
                AddEntryToCell(row.Entries[entryIndex], row.CellId);
        }

        if (state.RetailPartArray is not null)
        {
            _entityRetailPartArrays[state.EntityId] = state.RetailPartArray;
            _retailCellArrayRoutes[state.EntityId] = state.RetailRoute;
        }
        if (state.RetailCellIds is not null)
            _retailCellArrays[state.EntityId] = state.RetailCellIds;
        for (int rowIndex = 0; rowIndex < state.RetailRows.Count; rowIndex++)
        {
            PreparedShadowRetailPartRows row = state.RetailRows[rowIndex];
            if (!_retailPartEntriesByCell.TryGetValue(
                    row.CellId,
                    out List<RetailPartEntry>? entries))
            {
                entries = new List<RetailPartEntry>();
                _retailPartEntriesByCell[row.CellId] = entries;
            }
            entries.AddRange(row.Entries);
        }
        BumpOwnerVersion(state.EntityId);
    }

    internal sealed class LandblockReplacementBuilder : IDisposable
    {
        private readonly ShadowObjectRegistry _active;
        private readonly ShadowObjectRegistry _staging;
        private readonly uint _prefix;
        private readonly IReadOnlyList<uint> _expected;
        private readonly List<uint>? _activeSlots;
        private readonly List<uint>? _stagingSlots;
        private readonly int _activeSlotLimit;
        private readonly int _stagingSlotLimit;
        private readonly HashSet<uint> _owners = new();
        private readonly List<uint> _ownerIds = new();
        private readonly List<PreparedShadowOwnerSlot> _states = new();
        private readonly Dictionary<uint, int> _stateIndex = new();
        private int _expectedIndex;
        private int _activeSlotIndex;
        private int _stagingSlotIndex;
        private int _ownerIndex;
        private int _phase;

        internal LandblockReplacementBuilder(
            ShadowObjectRegistry active,
            ShadowObjectRegistry staging,
            uint landblockId,
            IReadOnlyList<uint> expected)
        {
            _active = active;
            _staging = staging;
            _prefix = landblockId & 0xFFFF0000u;
            _expected = expected;
            active._prefixOwnerSlots.TryGetValue(
                _prefix,
                out _activeSlots);
            staging._prefixOwnerSlots.TryGetValue(
                _prefix,
                out _stagingSlots);
            _activeSlotLimit = _activeSlots?.Count ?? 0;
            _stagingSlotLimit = _stagingSlots?.Count ?? 0;
        }

        internal int WorkUnits { get; private set; }
        internal PreparedLandblockShadowReplacement? Prepared { get; private set; }

        internal bool Advance()
        {
            switch (_phase)
            {
                case 0:
                    if (_expectedIndex < _expected.Count)
                    {
                        AddOwner(_expected[_expectedIndex++]);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                case 1:
                    if (_activeSlotIndex < _activeSlotLimit)
                    {
                        uint ownerId = _activeSlots![_activeSlotIndex++];
                        if (_active._entityReg.TryGetValue(
                                ownerId,
                                out RegistrationRecord? registration)
                            && registration.IsStatic
                            && (registration.SeedCellId & 0xFFFF0000u) == _prefix)
                        {
                            AddOwner(ownerId);
                        }
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                case 2:
                    if (_stagingSlotIndex < _stagingSlotLimit)
                    {
                        uint ownerId = _stagingSlots![_stagingSlotIndex++];
                        if (_staging._entityReg.ContainsKey(ownerId))
                            AddOwner(ownerId);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                case 3:
                    if (_ownerIndex < _ownerIds.Count)
                    {
                        uint ownerId = _ownerIds[_ownerIndex++];
                        _staging.TryCaptureOwnerState(
                            ownerId,
                            out PreparedShadowOwnerState? state);
                        _stateIndex[ownerId] = _states.Count;
                        _states.Add(new PreparedShadowOwnerSlot(ownerId, state));
                        WorkUnits++;
                        return false;
                    }
                    Prepared = new PreparedLandblockShadowReplacement(
                        _prefix,
                        _ownerIds,
                        _states);
                    _phase++;
                    return true;
                default:
                    return true;
            }
        }

        internal void AddOwner(uint ownerId)
        {
            if (_owners.Add(ownerId))
                _ownerIds.Add(ownerId);
        }

        internal void RefreshOwner(uint ownerId)
        {
            AddOwner(ownerId);
            if (_stateIndex.TryGetValue(ownerId, out int index))
            {
                _staging.TryCaptureOwnerState(
                    ownerId,
                    out PreparedShadowOwnerState? state);
                _states[index].State = state;
                return;
            }
            if (_phase > 3)
            {
                _staging.TryCaptureOwnerState(
                    ownerId,
                    out PreparedShadowOwnerState? state);
                _stateIndex[ownerId] = _states.Count;
                _states.Add(new PreparedShadowOwnerSlot(ownerId, state));
            }
        }

        public void Dispose() { }
    }

    private bool IsRetainedRefloodOwner(uint ownerId, uint landblockId)
    {
        if (!_entityReg.TryGetValue(ownerId, out RegistrationRecord? registration)
            || _suspendedEntities.Contains(ownerId)
            || (registration.IsStatic
                && (registration.SeedCellId & 0xFFFF0000u)
                    == (landblockId & 0xFFFF0000u)))
        {
            return false;
        }
        return OwnerTouchesLandblock(ownerId, landblockId);
    }

    internal sealed class PreparedLandblockShadowReplacement
    {
        internal PreparedLandblockShadowReplacement(
            uint landblockPrefix,
            IReadOnlyList<uint> ownerIds,
            IReadOnlyList<PreparedShadowOwnerSlot> ownerStates)
        {
            LandblockPrefix = landblockPrefix;
            OwnerIds = ownerIds;
            OwnerStates = ownerStates;
        }

        internal uint LandblockPrefix { get; }
        internal IReadOnlyList<uint> OwnerIds { get; }
        internal IReadOnlyList<PreparedShadowOwnerSlot> OwnerStates { get; }
    }

    internal sealed class PreparedShadowOwnerSlot
    {
        internal PreparedShadowOwnerSlot(
            uint entityId,
            PreparedShadowOwnerState? state)
        {
            EntityId = entityId;
            State = state;
        }

        internal uint EntityId { get; }
        internal PreparedShadowOwnerState? State { get; set; }
    }

    internal sealed record PreparedShadowOwnerState(
        uint EntityId,
        RegistrationRecord Registration,
        IReadOnlyList<ShadowShape>? Shapes,
        List<uint>? CellIds,
        IReadOnlyList<PreparedShadowCellRows> Rows,
        bool Suspended,
        List<uint>? SuspendedCellIds,
        HashSet<uint>? WithdrawnPrefixes,
        IReadOnlyList<ShadowShape>? RetailPartArray,
        List<uint>? RetailCellIds,
        RetailCellArrayRoute RetailRoute,
        IReadOnlyList<PreparedShadowRetailPartRows> RetailRows);

    internal sealed record PreparedShadowCellRows(
        uint CellId,
        ShadowEntry[] Entries);

    internal sealed record PreparedShadowRetailPartRows(
        uint CellId,
        RetailPartEntry[] Entries);

    public void Clear()
    {
        bool mutated = _cells.Count != 0
            || _entityToCells.Count != 0
            || _entityReg.Count != 0
            || _suspendedEntities.Count != 0
            || _suspendedEntityCells.Count != 0
            || _nextPreparedSetPositionCommitId
                != _lastAppliedSetPositionCommitId;
        if (mutated)
            AdvanceMutationRevision();
        _cells.Clear();
        _entityToCells.Clear();
        _suspendedEntities.Clear();
        _suspendedEntityCells.Clear();
        _withdrawnPrefixesByOwner.Clear();
        _entityShapes.Clear();
        _entityReg.Clear();
        _ownerVersions.Clear();
        _ownerPrefixes.Clear();
        _prefixOwnerSlots.Clear();
        _prefixOwnerIndices.Clear();
        _prefixFreeSlots.Clear();
        _ownerSlots.Clear();
        _ownerIndices.Clear();
        _ownerFreeSlots.Clear();
        _prefixScratch.Clear();
        _removedPrefixScratch.Clear();
        _pendingSetPositionDispatches.Clear();
        _entityRetailPartArrays.Clear();
        _retailCellArrays.Clear();
        _retailCellArrayRoutes.Clear();
        _retailPartEntriesByCell.Clear();
        _childParent.Clear();
        _parentChildren.Clear();
        _childPartArrays.Clear();
        _fallback = null;
    }

    public IEnumerable<ShadowEntry> AllEntriesForDebug()
    {
        var seenEntities = new HashSet<uint>();
        foreach (var kvp in _entityToCells)
        {
            uint entityId = kvp.Key;
            if (!seenEntities.Add(entityId)) continue;

            foreach (uint cellId in kvp.Value)
            {
                if (!_cells.TryGetValue(cellId, out var list)) continue;
                bool anyFound = false;
                foreach (var entry in list)
                {
                    if (entry.EntityId == entityId)
                    {
                        yield return entry;
                        anyFound = true;
                    }
                }
                if (anyFound) break;
            }
        }
    }
}

public enum ShadowCollisionType : byte { BSP, Cylinder, Sphere }

public readonly record struct ShadowEntry(
    uint EntityId,
    uint GfxObjId,
    Vector3 Position,
    Quaternion Rotation,
    float Radius,
    ShadowCollisionType CollisionType = ShadowCollisionType.BSP,
    float CylHeight = 0f,
    float Scale = 1.0f,
    uint State = 0u,
    EntityCollisionFlags Flags = EntityCollisionFlags.None,
    Vector3 LocalPosition = default,
    Quaternion LocalRotation = default);

public enum RetailCellArrayRoute
{
    None,

    Cylsphere,

    BoundingBox,
}

public readonly record struct RetailPartEntry(
    uint EntityId,
    int PartIndex,
    uint GfxObjId,
    uint CellId,
    bool ClipPlanesRequired);
