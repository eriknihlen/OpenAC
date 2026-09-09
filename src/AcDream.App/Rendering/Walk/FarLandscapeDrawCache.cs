using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Walk;

/// <summary>Keeps coarse landscape mesh groups until their source records change.</summary>
internal sealed class FarLandscapeDrawCache(
    WbDrawDispatcher dispatcher, IWalkFrameWorldData world)
{
    private readonly record struct CellKey(uint Block, int Side, int Index);
    private readonly record struct BatchRef(Entity Entity, int PartIndex, int BatchIndex);

    private sealed class Entity(RenderProjectionRecord record, uint cellId)
    {
        public RenderProjectionRecord Record = record;
        public readonly uint CellId = cellId;
        public readonly List<WbDrawDispatcher.WalkClassifiedBatch> Batches = new();
        public readonly List<WbDrawDispatcher.WalkCachedPart> Parts = new();
        public bool[] Visible = [];
        public WbDrawDispatcher.InstanceLightSet Lights;
        public uint Indoor;
        public Vector2 Selection;
    }

    private sealed class Entry(uint[] cells)
    {
        public readonly uint[] Cells = cells;
        public readonly ulong[] Revisions = new ulong[cells.Length];
        public readonly List<Entity> Entities = new();
        public readonly List<BatchRef> Opaque = new();
        public readonly List<BatchRef> Alpha = new();
        public readonly Dictionary<GroupKey, List<BatchRef>> Groups = new();
        public readonly List<List<BatchRef>> GroupLists = new();
        public long MeshVersion = -1;
        public bool Retry;
    }

    private readonly Dictionary<CellKey, Entry> _entries = new();
    private readonly List<CellKey> _expired = new();
    private readonly List<WbDrawDispatcher.WalkClassifiedSelectionPart> _selectionScratch = new();
    private readonly List<WbDrawDispatcher.WalkClassifiedBatch> _alphaScratch = new();
    private readonly int[] _alphaEnds = new int[16];
    private (RenderSceneGeneration Generation, uint TupleLandblockId) _context;

    internal int RebuildCount { get; private set; }
    internal int EntityClassificationCount { get; private set; }
    internal int EntryCount => _entries.Count;
    internal ReadOnlySpan<int> AlphaEnds => _alphaEnds;

    internal void Clear()
    {
        _entries.Clear();
        _expired.Clear();
        _alphaScratch.Clear();
    }

    internal void BeginFrame()
    {
        if (_context != world.RetainedContext)
        {
            Clear();
            _context = world.RetainedContext;
        }

        // Drop departed content even when the camera never revisits its cells.
        _expired.Clear();
        foreach ((CellKey key, Entry entry) in _entries)
        {
            bool populated = false;
            bool wasPopulated = false;
            foreach (ulong revision in entry.Revisions)
                wasPopulated |= revision != 0;
            if (!wasPopulated)
                continue;
            foreach (uint cell in entry.Cells)
            {
                if (world.GetOutdoorCellRenderRevision(cell) is > 0)
                {
                    populated = true;
                    break;
                }
            }
            if (!populated)
                _expired.Add(key);
        }
        foreach (CellKey key in _expired)
            _entries.Remove(key);
    }

    internal bool TryAppend(
        uint block, int side, int index,
        OrderedDrawStream stream, IWalkLookInViewSource views, int route,
        Vector3 camera, List<WbDrawDispatcher.WalkClassifiedBatch> alpha)
    {
        int span = 8 / side;
        int firstX = index / side * span;
        int firstY = index % side * span;
        uint firstCell = block | (uint)(firstX * 8 + firstY + 1);
        if (world.GetOutdoorCellRenderRevision(firstCell) is null)
            return false;

        CellKey key = new(block, side, index);
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            uint[] cells = new uint[span * span];
            int cursor = 0;
            for (int x = firstX; x < firstX + span; x++)
            for (int y = firstY; y < firstY + span; y++)
                cells[cursor++] = block | (uint)(x * 8 + y + 1);
            entry = new Entry(cells);
            _entries.Add(key, entry);
        }

        if (NeedsRebuild(entry))
            Rebuild(entry);
        else
            RefreshEntities(entry);

        foreach (Entity entity in entry.Entities)
        {
            dispatcher.ResolveCachedWalkLighting(
                in entity.Record, _context.TupleLandblockId,
                out entity.Lights, out entity.Indoor, out entity.Selection);
            for (int i = 0; i < entity.Parts.Count; i++)
            {
                WbDrawDispatcher.WalkCachedPart part = entity.Parts[i];
                bool visible = dispatcher.AdmitCachedWalkPart(
                    in entity.Record, in part, views, route);
                entity.Visible[i] = visible;
                if (visible)
                {
                    var selection = part.Selection;
                    dispatcher.PublishWalkSelectionPart(in selection);
                }
            }
        }

        foreach (BatchRef item in entry.Opaque)
        {
            Entity entity = item.Entity;
            if (!entity.Visible[item.PartIndex])
                continue;
            WbDrawDispatcher.WalkClassifiedBatch batch = entity.Batches[item.BatchIndex];
            WalkDrawStage stage = IsDynamic(entity.Record)
                ? WalkDrawStage.Dynamic : WalkDrawStage.OutdoorStatic;
            stream.Append(new OrderedDrawCommand(
                batch.Key, batch.Transform, stage, firstCell, batch.ClipSlot,
                entity.Lights, entity.Indoor, batch.Alpha,
                entity.Selection, batch.DetailCategory, AllowInstanceMerge: true));
        }

        for (int cellIndex = 0; cellIndex < entry.Cells.Length; cellIndex++)
        {
            _alphaScratch.Clear();
            foreach (BatchRef item in entry.Alpha)
            {
                Entity entity = item.Entity;
                if (entity.CellId != entry.Cells[cellIndex] || !entity.Visible[item.PartIndex])
                    continue;
                WbDrawDispatcher.WalkClassifiedBatch batch = entity.Batches[item.BatchIndex];
                _alphaScratch.Add(batch with
                {
                    Lights = entity.Lights,
                    IndoorFlag = entity.Indoor,
                    SelectionLighting = entity.Selection,
                    SortDistanceSq = Vector3.DistanceSquared(
                        Vector3.Transform(batch.LocalSortCenter, batch.Transform), camera),
                });
            }
            _alphaScratch.Sort(static (a, b) => b.SortDistanceSq.CompareTo(a.SortDistanceSq));
            alpha.AddRange(_alphaScratch);
            _alphaEnds[cellIndex] = alpha.Count;
        }
        return true;
    }

    private bool NeedsRebuild(Entry entry)
    {
        if (entry.Retry || entry.MeshVersion != dispatcher.WalkMeshAvailabilityVersion)
            return true;
        for (int i = 0; i < entry.Cells.Length; i++)
        {
            if (entry.Revisions[i] != world.GetOutdoorCellRenderRevision(entry.Cells[i]))
                return true;
        }
        return false;
    }

    private void Rebuild(Entry entry)
    {
        entry.Entities.Clear();
        entry.Opaque.Clear();
        entry.Alpha.Clear();
        entry.Retry = true;
        bool retry = false;
        entry.MeshVersion = dispatcher.WalkMeshAvailabilityVersion;
        var seen = new HashSet<RenderProjectionId>();
        for (int i = 0; i < entry.Cells.Length; i++)
        {
            uint cellId = entry.Cells[i];
            entry.Revisions[i] = world.GetOutdoorCellRenderRevision(cellId) ?? 0;
            WalkFrameStaticRecords records = world.GetOutdoorObjects(cellId);
            retry |= !records.IsComplete;
            foreach (RenderProjectionRecord record in records.Records)
            {
                if (!seen.Add(record.Id))
                    continue;
                var entity = new Entity(record, cellId);
                retry |= ClassifyEntity(entity, records.TupleLandblockId);
                entry.Entities.Add(entity);
            }
        }
        Regroup(entry);
        entry.Retry = retry;
        RebuildCount++;
    }

    private void RefreshEntities(Entry entry)
    {
        bool changed = false;
        bool retry = false;
        foreach (Entity entity in entry.Entities)
        {
            if (!world.TryGetCurrentProjection(entity.Record.Source.LocalEntityId, out var current)
                || current.Id != entity.Record.Id
                || current.OwnerIncarnation != entity.Record.OwnerIncarnation)
            {
                Rebuild(entry);
                return;
            }
            if (current == entity.Record && !IsDynamic(current))
                continue;
            entry.Retry = true;
            changed = true;
            entity.Record = current;
            retry |= ClassifyEntity(entity, _context.TupleLandblockId);
        }
        if (changed)
        {
            Regroup(entry);
            entry.Retry = retry;
        }
    }

    private bool ClassifyEntity(Entity entity, uint tupleLandblockId)
    {
        entity.Batches.Clear();
        entity.Parts.Clear();
        _selectionScratch.Clear();
        dispatcher.ClassifyEntityForWalk(
            in entity.Record, tupleLandblockId,
            entity.Batches, _selectionScratch, liveDynamic: IsDynamic(entity.Record),
            retainedParts: entity.Parts);
        if (entity.Visible.Length < entity.Parts.Count)
            entity.Visible = new bool[entity.Parts.Count];
        EntityClassificationCount++;
        return dispatcher.WalkClassificationPending;
    }

    private static void Regroup(Entry entry)
    {
        entry.Opaque.Clear();
        entry.Alpha.Clear();
        entry.Groups.Clear();
        foreach (List<BatchRef> group in entry.GroupLists)
            group.Clear();
        int groupCount = 0;
        foreach (Entity entity in entry.Entities)
        {
            for (int partIndex = 0; partIndex < entity.Parts.Count; partIndex++)
            {
                var part = entity.Parts[partIndex];
                for (int b = part.BatchStart; b < part.BatchStart + part.BatchCount; b++)
                {
                    var batch = entity.Batches[b];
                    BatchRef item = new(entity, partIndex, b);
                    if (!batch.IsOpaque)
                    {
                        entry.Alpha.Add(item);
                        continue;
                    }
                    if (!entry.Groups.TryGetValue(batch.Key, out List<BatchRef>? group))
                    {
                        if (groupCount == entry.GroupLists.Count)
                            entry.GroupLists.Add(new List<BatchRef>());
                        group = entry.GroupLists[groupCount++];
                        entry.Groups.Add(batch.Key, group);
                    }
                    group.Add(item);
                }
            }
        }
        for (int i = 0; i < groupCount; i++)
            entry.Opaque.AddRange(entry.GroupLists[i]);
    }

    private static bool IsDynamic(in RenderProjectionRecord record) =>
        record.ProjectionClass is RenderProjectionClass.LiveDynamicRoot
            or RenderProjectionClass.EquippedChild;
}
