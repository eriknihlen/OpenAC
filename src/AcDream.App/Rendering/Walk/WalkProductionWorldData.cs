using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using AcDream.App.Rendering.Scene;
using AcDream.Core.Physics;

namespace AcDream.App.Rendering.Walk;

internal sealed class WalkProductionWorldData : IWalkFrameWorldData
{
    private readonly WalkBuildingRegistry _buildings;
    private readonly ShadowObjectRegistry _shadows;
    private RenderSceneQuery _scene;
    private uint _tupleLandblockId;

    private readonly Dictionary<uint, WalkFrameStaticRecords> _cellCache = new();
    private readonly Dictionary<uint, WalkFrameStaticRecords> _cellDynamicCache = new();
    private readonly Dictionary<uint, WalkFrameStaticRecords> _cellObjectsCache = new();
    private readonly Dictionary<uint, List<RenderProjectionRecord>> _shellsByAnchor = new();
    private readonly Dictionary<uint, WalkFrameStaticRecords> _outdoorMaterialized = new();
    private readonly Dictionary<uint, WalkFrameStaticRecords> _outdoorDynamicsMaterialized = new();
    private readonly Dictionary<uint, WalkFrameStaticRecords> _outdoorObjectsMaterialized = new();
    private readonly Dictionary<uint, WalkFrameStaticRecords> _shellMaterialized = new();

    private readonly HashSet<uint> _unregisteredEntitiesThisFrame = new();

    private RenderProjectionRecord[] _sweepScratch = new RenderProjectionRecord[1024];

    private RenderProjectionRecord[] _cellViewScratch = new RenderProjectionRecord[64];

    private RenderProjectionRecord[] _arena = new RenderProjectionRecord[4096];
    private int _arenaLength;

    internal WalkProductionWorldData(
        WalkBuildingRegistry buildings,
        ShadowObjectRegistry shadows)
    {
        _buildings = buildings ?? throw new ArgumentNullException(nameof(buildings));
        _shadows = shadows ?? throw new ArgumentNullException(nameof(shadows));
    }

    public int UnregisteredRenderMembershipCount { get; private set; }

    internal void BeginFrame(
        RenderSceneQuery scene,
        uint tupleLandblockId,
        int renderCenterLbX,
        int renderCenterLbY)
    {
        _scene = scene;
        _tupleLandblockId = tupleLandblockId;
        _cellCache.Clear();
        _cellDynamicCache.Clear();
        _cellObjectsCache.Clear();
        _outdoorMaterialized.Clear();
        _outdoorDynamicsMaterialized.Clear();
        _outdoorObjectsMaterialized.Clear();
        _shellMaterialized.Clear();
        _arenaLength = 0;
        UnregisteredRenderMembershipCount = 0;
        _unregisteredEntitiesThisFrame.Clear();
        foreach (List<RenderProjectionRecord> bucket in _shellsByAnchor.Values)
            bucket.Clear();

        int required = _scene.IndexCounts.For(RenderSceneIndex.OutdoorStatic);
        if (required > _sweepScratch.Length)
        {
            _sweepScratch = new RenderProjectionRecord[
                Math.Max(required, _sweepScratch.Length * 2)];
        }
        int count = _scene.CopyIndexTo(RenderSceneIndex.OutdoorStatic, _sweepScratch);
        for (int i = 0; i < count; i++)
        {
            ref readonly RenderProjectionRecord record = ref _sweepScratch[i];
            if (!record.EntityPayload.IsBuildingShell)
                continue;
            uint anchor = BuildingShellBucketCellId(in record);
            if (!_shellsByAnchor.TryGetValue(anchor, out List<RenderProjectionRecord>? shells))
                _shellsByAnchor[anchor] = shells = new List<RenderProjectionRecord>();
            shells.Add(record);
        }
    }

    public WalkFrameStaticRecords GetCellStatics(uint cellId)
    {
        if (_cellCache.TryGetValue(cellId, out WalkFrameStaticRecords cached))
            return cached;
        WalkFrameStaticRecords records = ResolveCellView(cellId, dynamic: false);
        _cellCache[cellId] = records;
        return records;
    }

    public WalkFrameStaticRecords GetCellObjects(uint cellId)
    {
        if (_cellObjectsCache.TryGetValue(cellId, out WalkFrameStaticRecords cached))
            return cached;
        WalkFrameStaticRecords records = ResolveCellView(cellId, dynamic: null);
        _cellObjectsCache[cellId] = records;
        return records;
    }

    public WalkFrameStaticRecords GetCellDynamics(uint cellId)
    {
        if (_cellDynamicCache.TryGetValue(cellId, out WalkFrameStaticRecords cached))
            return cached;
        WalkFrameStaticRecords records = ResolveCellView(cellId, dynamic: true);
        _cellDynamicCache[cellId] = records;
        return records;
    }

    public WalkFrameStaticRecords GetOutdoorStatics(uint cellId)
    {
        if (_outdoorMaterialized.TryGetValue(cellId, out WalkFrameStaticRecords cached))
            return cached;
        WalkFrameStaticRecords records = ResolveCellView(cellId, dynamic: false);
        _outdoorMaterialized[cellId] = records;
        return records;
    }

    public WalkFrameStaticRecords GetOutdoorDynamics(uint cellId)
    {
        if (_outdoorDynamicsMaterialized.TryGetValue(
                cellId,
                out WalkFrameStaticRecords cached))
        {
            return cached;
        }

        WalkFrameStaticRecords records = ResolveCellView(cellId, dynamic: true);
        _outdoorDynamicsMaterialized[cellId] = records;
        return records;
    }

    public WalkFrameStaticRecords GetOutdoorObjects(uint cellId)
    {
        if (_outdoorObjectsMaterialized.TryGetValue(
                cellId,
                out WalkFrameStaticRecords cached))
        {
            return cached;
        }

        WalkFrameStaticRecords records = ResolveCellView(cellId, dynamic: null);
        _outdoorObjectsMaterialized[cellId] = records;
        return records;
    }

    private WalkFrameStaticRecords ResolveCellView(uint cellId, bool? dynamic)
    {
        IReadOnlyList<RetailPartEntry> entries =
            _shadows.GetRetailPartEntriesInCell(cellId);
        if (entries.Count == 0)
            return WalkFrameStaticRecords.Empty with { TupleLandblockId = _tupleLandblockId };

        int written = 0;
        uint previousEntityId = 0;
        bool havePrevious = false;
        for (int i = 0; i < entries.Count; i++)
        {
            uint entityId = entries[i].EntityId;
            if (havePrevious && entityId == previousEntityId)
                continue;
            previousEntityId = entityId;
            havePrevious = true;

            if (!_scene.TryGetByLocalEntityId(entityId, out RenderProjectionRecord record))
            {
                if (_unregisteredEntitiesThisFrame.Add(entityId))
                    UnregisteredRenderMembershipCount++;
                continue;
            }
            if (record.EntityPayload.IsBuildingShell)
                continue; // buildings draw at their own shell turn.
            if (dynamic.HasValue
                && IsDynamicProjectionClass(record.ProjectionClass) != dynamic.Value)
                continue;

            if (written == _cellViewScratch.Length)
            {
                var grown = new RenderProjectionRecord[_cellViewScratch.Length * 2];
                Array.Copy(_cellViewScratch, grown, written);
                _cellViewScratch = grown;
            }
            _cellViewScratch[written++] = record;
        }

        return written == 0
            ? WalkFrameStaticRecords.Empty with { TupleLandblockId = _tupleLandblockId }
            : new WalkFrameStaticRecords(
                AppendToArena(_cellViewScratch.AsSpan(0, written)), _tupleLandblockId);
    }

    private static bool IsDynamicProjectionClass(RenderProjectionClass projectionClass) =>
        projectionClass is RenderProjectionClass.LiveDynamicRoot
            or RenderProjectionClass.EquippedChild;

    public WalkFrameStaticRecords GetBuildingShellStatics(WalkBuilding building)
    {
        uint anchor = BuildingShellBucketCellId(building);
        if (anchor == 0)
            return WalkFrameStaticRecords.Empty with { TupleLandblockId = _tupleLandblockId };
        if (_shellMaterialized.TryGetValue(anchor, out WalkFrameStaticRecords cached))
            return cached;
        WalkFrameStaticRecords records =
            _shellsByAnchor.TryGetValue(anchor, out List<RenderProjectionRecord>? shells)
                && shells.Count > 0
                ? new WalkFrameStaticRecords(
                    AppendToArena(CollectionsMarshal.AsSpan(shells)), _tupleLandblockId)
                : WalkFrameStaticRecords.Empty with { TupleLandblockId = _tupleLandblockId };
        _shellMaterialized[anchor] = records;
        return records;
    }

    private ArraySegment<RenderProjectionRecord> AppendToArena(
        ReadOnlySpan<RenderProjectionRecord> source)
    {
        if (source.Length == 0)
            return ArraySegment<RenderProjectionRecord>.Empty;

        int required = _arenaLength + source.Length;
        if (required > _arena.Length)
        {
            var grown = new RenderProjectionRecord[Math.Max(required, _arena.Length * 2)];
            Array.Copy(_arena, grown, _arenaLength);
            _arena = grown;
        }

        source.CopyTo(_arena.AsSpan(_arenaLength, source.Length));
        var segment = new ArraySegment<RenderProjectionRecord>(_arena, _arenaLength, source.Length);
        _arenaLength += source.Length;
        return segment;
    }

    internal static uint AnchorCellId(WalkBuilding building)
    {
        foreach (ref readonly WalkBldPortal portal in building.Portals.AsSpan())
        {
            if (portal.OtherCellId != 0xFFFFFFFFu)
                return portal.OtherCellId;
        }
        return 0;
    }

    internal static uint BuildingShellBucketCellId(in RenderProjectionRecord record)
        => record.Source.BuildingShellAnchorCellId != 0
            ? record.Source.BuildingShellAnchorCellId
            : record.Source.EffectCellId;

    internal static uint BuildingShellBucketCellId(WalkBuilding building)
    {
        ArgumentNullException.ThrowIfNull(building);
        uint anchor = AnchorCellId(building);
        return anchor != 0 ? anchor : building.PositionCellId;
    }

    public Matrix4x4 GetBuildingWorldTransform(WalkBuilding building)
    {
        if (!_buildings.TryGetEntry(building, out WalkBuildingFactory.Entry? entry))
        {
            throw new InvalidOperationException(
                $"walk building 0x{building.PositionCellId:X8} has no committed registry entry");
        }
        return entry.PartZeroWorldTransform;
    }
}
