using System.Runtime.CompilerServices;
using AcDream.App.Rendering.Wb;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Vfx;

namespace AcDream.App.Rendering;

internal readonly record struct DirectionalShadowTerrainRange(
    uint FirstIndex,
    int IndexCount,
    uint LandblockId = 0u);

internal readonly record struct DirectionalShadowTerrainGeometry(
    IGpuBuffer VertexBuffer,
    IGpuBuffer IndexBuffer);

internal sealed class DirectionalShadowTerrainPreparedDraws
{
    private DirectionalShadowTerrainRange[] _ranges = [];
    private DrawElementsIndirectCommand[] _activeCommands = [];
    private int _rangeCount;
    private int _activeCount;
    private bool _building;

    public long SourceFrameSequence { get; private set; }

    public ulong BuildSequence { get; private set; }

    public ulong ActiveSelectionSequence { get; private set; }

    public ReadOnlySpan<DrawElementsIndirectCommand> Commands =>
        _activeCommands.AsSpan(0, _activeCount);

    public ReadOnlySpan<DirectionalShadowTerrainRange> ResidentRanges =>
        _ranges.AsSpan(0, _rangeCount);

    public long RetainedScratchBytes =>
        checked(
            (long)_ranges.Length
                * Unsafe.SizeOf<DirectionalShadowTerrainRange>()
            + (long)_activeCommands.Length
                * Unsafe.SizeOf<DrawElementsIndirectCommand>());

    public bool TryBegin(long frameSequence, int estimatedCommands)
    {
        if (_building)
            throw new InvalidOperationException(
                "A terrain shadow draw build is already active.");
        if (frameSequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSequence));
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedCommands);
        if (SourceFrameSequence == frameSequence)
            return false;

        EnsureCapacity(estimatedCommands);
        _rangeCount = 0;
        _building = true;
        return true;
    }

    public void Add(in DirectionalShadowTerrainRange range)
    {
        if (!_building)
            throw new InvalidOperationException(
                "Begin a terrain shadow draw build before adding ranges.");
        if (range.IndexCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(range));
        EnsureCapacity(checked(_rangeCount + 1));
        _ranges[_rangeCount++] = range;
    }

    public void Complete(long frameSequence)
    {
        if (!_building)
            throw new InvalidOperationException(
                "No terrain shadow draw build is active.");
        if (frameSequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSequence));
        SourceFrameSequence = frameSequence;
        BuildSequence = checked(BuildSequence + 1);
        _building = false;
        RebuildActiveAll();
    }

    internal void ApplySelection(in RetailLandscapeVisibilityFrame visibility)
    {
        IReadOnlySet<uint> visible = visibility.CellIds
            ?? RetailLandscapeVisibilityFrame.None.CellIds;
        _activeCount = 0;
        if (visibility.HasCompletedWorldView)
        {
            for (int rangeIndex = 0; rangeIndex < _rangeCount; rangeIndex++)
            {
                DirectionalShadowTerrainRange range = _ranges[rangeIndex];
                uint prefix = range.LandblockId & 0xFFFF0000u;
                bool selected = false;
                for (uint low = 1u; low <= 64u; low++)
                {
                    if (visible.Contains(prefix | low))
                    {
                        selected = true;
                        break;
                    }
                }
                if (selected)
                    Emit(in range);
            }
        }
        ActiveSelectionSequence = checked(ActiveSelectionSequence + 1);
    }

    private void RebuildActiveAll()
    {
        EnsureCapacity(_rangeCount);
        _activeCount = 0;
        for (int rangeIndex = 0; rangeIndex < _rangeCount; rangeIndex++)
            Emit(in _ranges[rangeIndex]);
        ActiveSelectionSequence = checked(ActiveSelectionSequence + 1);
    }

    private void Emit(in DirectionalShadowTerrainRange range)
    {
        _activeCommands[_activeCount++] = new DrawElementsIndirectCommand
        {
            Count = checked((uint)range.IndexCount),
            InstanceCount = 1,
            FirstIndex = range.FirstIndex,
            BaseVertex = 0,
            BaseInstance = 0,
        };
    }

    public void Abort()
    {
        _rangeCount = 0;
        _activeCount = 0;
        _building = false;
    }

    private void EnsureCapacity(int required)
    {
        if (_ranges.Length >= required)
            return;
        int capacity = _ranges.Length == 0 ? 16 : _ranges.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Array.Resize(ref _ranges, capacity);
        Array.Resize(ref _activeCommands, capacity);
    }
}

public sealed partial class TerrainModernRenderer
{
    private readonly DirectionalShadowTerrainPreparedDraws
        _directionalShadowTerrainDraws = new();
    private long _directionalShadowFrameSequence;
    private uint[] _directionalShadowSlotLandblocks = [];
    private uint[] _directionalShadowSlotFirstIndices = [];
    private int[] _directionalShadowSlotIndexCounts = [];
    private bool[] _directionalShadowSlotPresent = [];
    private bool _directionalShadowTopologySnapshotValid;
    private long _directionalShadowTopologySequence;

    internal DirectionalShadowTerrainGeometry GetDirectionalShadowGeometry() => new(
        _vertexStore ?? throw new InvalidOperationException("Terrain has no vertex store."),
        _indexStore ?? throw new InvalidOperationException("Terrain has no index store."));

    internal DirectionalShadowTerrainPreparedDraws
        PrepareDirectionalShadowDraws(
            in RetailLandscapeVisibilityFrame visibility)
    {
        EnsureDirectionalShadowSlotCapacity(_slots.Length);
        bool topologyChanged = !_directionalShadowTopologySnapshotValid;
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            SlotData? data = _slots[slot];
            bool present = data is not null;
            if (_directionalShadowSlotPresent[slot] != present
                || present
                    && (_directionalShadowSlotLandblocks[slot] != data!.LandblockId
                        || _directionalShadowSlotFirstIndices[slot] != data.FirstIndex
                        || _directionalShadowSlotIndexCounts[slot] != data.IndexCount))
            {
                topologyChanged = true;
            }
        }

        if (topologyChanged)
        {
            _directionalShadowTopologySequence = checked(
                _directionalShadowTopologySequence + 1);
            _directionalShadowTerrainDraws.TryBegin(
                _directionalShadowTopologySequence,
                _alloc.LoadedCount);
            try
            {
                for (int slot = 0; slot < _slots.Length; slot++)
                {
                    SlotData? data = _slots[slot];
                    bool present = data is not null;
                    _directionalShadowSlotPresent[slot] = present;
                    if (!present)
                    {
                        _directionalShadowSlotLandblocks[slot] = 0u;
                        _directionalShadowSlotFirstIndices[slot] = 0u;
                        _directionalShadowSlotIndexCounts[slot] = 0;
                        continue;
                    }
                    _directionalShadowSlotLandblocks[slot] = data!.LandblockId;
                    _directionalShadowSlotFirstIndices[slot] = data.FirstIndex;
                    _directionalShadowSlotIndexCounts[slot] = data.IndexCount;
                    var range = new DirectionalShadowTerrainRange(
                        data.FirstIndex,
                        data.IndexCount,
                        data.LandblockId);
                    _directionalShadowTerrainDraws.Add(in range);
                }
                _directionalShadowTerrainDraws.Complete(
                    _directionalShadowTopologySequence);
                _directionalShadowTopologySnapshotValid = true;
            }
            catch
            {
                // The snapshot fields are populated while the retained
                // product is built. If publication fails, force the next
                // frame to retry even when those fields already match the
                // live slots; an aborted build is never a valid topology.
                _directionalShadowTopologySnapshotValid = false;
                _directionalShadowTerrainDraws.Abort();
                throw;
            }
        }

        _directionalShadowTerrainDraws.ApplySelection(in visibility);
        return _directionalShadowTerrainDraws;
    }

    private void EnsureDirectionalShadowSlotCapacity(int required)
    {
        if (_directionalShadowSlotPresent.Length >= required)
            return;
        int capacity = _directionalShadowSlotPresent.Length == 0
            ? 16
            : _directionalShadowSlotPresent.Length;
        while (capacity < required)
            capacity = checked(capacity * 2);
        Array.Resize(ref _directionalShadowSlotLandblocks, capacity);
        Array.Resize(ref _directionalShadowSlotFirstIndices, capacity);
        Array.Resize(ref _directionalShadowSlotIndexCounts, capacity);
        Array.Resize(ref _directionalShadowSlotPresent, capacity);
    }
}
