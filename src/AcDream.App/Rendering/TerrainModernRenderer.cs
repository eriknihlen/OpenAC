using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Terrain;

namespace AcDream.App.Rendering;

public sealed partial class TerrainModernRenderer : IDisposable
{
    private const int VertsPerLandblock = LandblockMesh.VerticesPerLandblock;
    private const int IndicesPerLandblock = VertsPerLandblock;
    private const int VertexSize = 40;  // sizeof(TerrainVertex)
    private const int IndexSize = sizeof(uint);
    private const float LandblockSize = LandblockMesh.LandblockSize;  // 192

    private readonly TerrainAtlas _atlas;

    public TerrainAtlas Atlas => _atlas;

    private readonly GpuRetiredTerrainSlotAllocator _alloc;
    private readonly GpuRetirementLedger _retirementLedger;
    private bool _disposed;

    // Per-slot live data (index by slot integer; null entries are unused slots).
    private SlotData?[] _slots;

    // Reverse map: landblockId -> slot, for RemoveLandblock and replacement.
    private readonly Dictionary<uint, int> _idToSlot = new();

    private long _globalVboCapacityBytes;
    private long _globalEboCapacityBytes;

    // Per-GPU-fenced-frame-slot draw bookkeeping, shared with the RHI arm.
    private int _dynamicFrameSlot;
    private bool _dynamicFrameStarted;

    internal int DynamicIndirectBufferCount => 0;

    // Reusable per-frame buffers.
    private readonly List<int> _visibleSlots = new();
    private readonly HashSet<uint> _walkVisibleLandblocks = new();
    private DrawElementsIndirectCommand[] _deicScratch = Array.Empty<DrawElementsIndirectCommand>();

    private readonly List<(int Start, int Count)> _cellRunScratch = new();

    private readonly List<(uint FirstIndex, int Count)> _batchRunScratch = new();

    private readonly HashSet<int> _walkSlotsThisFrame = new();
    private int _walkDrawsThisFrame;

    // Diag.
    public int LoadedSlots   => _alloc.LoadedCount;
    public int VisibleSlots  => _visibleSlots.Count;
    public int CapacitySlots => _alloc.Capacity;

    internal int WalkVisibleSlotCount => _walkSlotsThisFrame.Count;

    internal int WalkDrawCount => _walkDrawsThisFrame;

    public void BeginFrame(int frameSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        if (_directionalShadowFrameSequence == long.MaxValue)
            throw new InvalidOperationException(
                "Directional-shadow terrain frame identity was exhausted.");
        _directionalShadowFrameSequence++;
        _retirementLedger.RetryPendingPublications();
        _dynamicFrameSlot = frameSlot;
        _dynamicFrameStarted = true;
        _walkSlotsThisFrame.Clear();
        _walkDrawsThisFrame = 0;
    }

    public void AddLandblockWithMesh(uint landblockId, LandblockMeshData meshData, Vector3 worldOrigin)
        => AddLandblock(landblockId, meshData, worldOrigin);

    public void AddLandblock(uint landblockId, LandblockMeshData meshData, Vector3 worldOrigin)
    {
        ArgumentNullException.ThrowIfNull(meshData);
        if (meshData.Vertices.Length != VertsPerLandblock)
            throw new ArgumentException(
                $"Expected {VertsPerLandblock} vertices, got {meshData.Vertices.Length}",
                nameof(meshData));
        if (meshData.Indices.Length != IndicesPerLandblock)
            throw new ArgumentException(
                $"Expected {IndicesPerLandblock} indices, got {meshData.Indices.Length}",
                nameof(meshData));

        _alloc.RetryPendingPublications();

        bool replacing = _idToSlot.TryGetValue(landblockId, out int replacedSlot);
        int slot = _alloc.Allocate(out var needsGrow);
        bool published = false;
        try
        {
            if (needsGrow)
            {
                int newCap = Math.Max(_alloc.Capacity * 2, slot + 1);
                EnsureCapacity(newCap);
            }

            var bakedVerts = new TerrainVertex[VertsPerLandblock];
            float zMin = float.MaxValue, zMax = float.MinValue;
            for (int i = 0; i < VertsPerLandblock; i++)
            {
                var v = meshData.Vertices[i];
                var worldPos = v.Position + worldOrigin;
                bakedVerts[i] = new TerrainVertex(worldPos, v.Normal, v.Data0, v.Data1, v.Data2, v.Data3);
                if (worldPos.Z < zMin) zMin = worldPos.Z;
                if (worldPos.Z > zMax) zMax = worldPos.Z;
            }
            if (zMin == float.MaxValue) { zMin = 0f; zMax = 0f; }

            // Bake baseVertex into indices on the CPU side (driver-portable pattern).
            uint baseVertex = (uint)(slot * VertsPerLandblock);
            var bakedIndices = new uint[IndicesPerLandblock];
            for (int i = 0; i < IndicesPerLandblock; i++)
                bakedIndices[i] = meshData.Indices[i] + baseVertex;

            UploadRhiLandblock(slot, bakedVerts, bakedIndices);

            _slots[slot] = new SlotData
            {
                LandblockId = landblockId,
                WorldOrigin = worldOrigin,
                FirstIndex  = (uint)(slot * IndicesPerLandblock),
                IndexCount  = IndicesPerLandblock,
                AabbMin = new Vector3(worldOrigin.X, worldOrigin.Y, zMin),
                AabbMax = new Vector3(worldOrigin.X + LandblockSize, worldOrigin.Y + LandblockSize, zMax),
            };
            _idToSlot[landblockId] = slot;
            published = true;

            if (replacing)
            {
                _slots[replacedSlot] = null;
                _alloc.FreeAfterGpuUse(replacedSlot);
            }
        }
        finally
        {
            if (!published)
                _alloc.ReleaseUnsubmitted(slot);
        }
    }

    public void RemoveLandblock(uint landblockId)
    {
        _alloc.RetryPendingPublications();
        if (!_idToSlot.TryGetValue(landblockId, out var slot))
            return;
        _idToSlot.Remove(landblockId);
        _slots[slot] = null;
        _alloc.FreeAfterGpuUse(slot);
    }

    public void Draw(
        ICamera camera,
        FrustumPlanes? frustum = null,
        uint? neverCullLandblockId = null,
        IReadOnlySet<uint>? inViewLandcells = null)
    {
        if (_alloc.LoadedCount == 0) return;

        Matrix4x4 viewProjection = camera.View * camera.Projection;

        _walkVisibleLandblocks.Clear();
        if (inViewLandcells is not null)
        {
            foreach (uint cellId in inViewLandcells)
            {
                _walkVisibleLandblocks.Add(cellId & 0xFFFF0000u);
            }
        }

        _visibleSlots.Clear();
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            var data = _slots[slot];
            if (data is null) continue;
            if (inViewLandcells is not null
                && !_walkVisibleLandblocks.Contains(data.LandblockId & 0xFFFF0000u))
            {
                continue;
            }
            if (frustum is not null && data.LandblockId != neverCullLandblockId)
            {
                if (!FrustumCuller.IsAabbVisible(frustum.Value, data.AabbMin, data.AabbMax))
                    continue;
            }
            _visibleSlots.Add(slot);
        }
        if (_visibleSlots.Count == 0) return;

        BuildIndirectCommands();
        if (!_dynamicFrameStarted)
            throw new InvalidOperationException("BeginFrame must be called before drawing terrain.");
        DrawRhi(viewProjection, _visibleSlots.Count);
    }

    /// <summary>
    /// Builds this frame's <c>DrawElementsIndirectCommand</c> array from the
    /// visible slot list. Pure CPU.
    /// </summary>
    private void BuildIndirectCommands()
    {
        if (_deicScratch.Length < _visibleSlots.Count)
            _deicScratch = new DrawElementsIndirectCommand[Math.Max(_visibleSlots.Count, 64)];
        for (int i = 0; i < _visibleSlots.Count; i++)
        {
            var data = _slots[_visibleSlots[i]]!;
            _deicScratch[i] = new DrawElementsIndirectCommand
            {
                Count         = (uint)data.IndexCount,
                InstanceCount = 1u,
                FirstIndex    = data.FirstIndex,
                BaseVertex    = 0,            // baked into indices on upload
                BaseInstance  = 0,
            };
        }
    }

    public void DrawLandCells(
        Matrix4x4 viewProjection,
        IReadOnlyList<(uint LandblockId, int SideCellCount, int CellIndex)> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0) return;
        if (!_dynamicFrameStarted)
            throw new InvalidOperationException("BeginFrame must be called before drawing terrain.");

        _batchRunScratch.Clear();
        for (int i = 0; i < cells.Count; i++)
        {
            (uint landblockId, int sideCellCount, int cellIndex) = cells[i];
            uint slotKey = (landblockId & 0xFFFF0000u) | 0xFFFFu;
            if (!_idToSlot.TryGetValue(slotKey, out int slot))
                continue;
            uint baseFirstIndex = (uint)(slot * IndicesPerLandblock);

            _cellRunScratch.Clear();
            AppendCellIndexRuns(sideCellCount, cellIndex, _cellRunScratch);
            for (int r = 0; r < _cellRunScratch.Count; r++)
            {
                (int start, int count) = _cellRunScratch[r];
                _batchRunScratch.Add((baseFirstIndex + (uint)start, count));
            }
            _walkSlotsThisFrame.Add(slot);
        }

        int commandCount = _batchRunScratch.Count;
        if (commandCount == 0) return;

        if (_deicScratch.Length < commandCount)
            _deicScratch = new DrawElementsIndirectCommand[Math.Max(commandCount, 64)];
        for (int i = 0; i < commandCount; i++)
        {
            (uint firstIndex, int count) = _batchRunScratch[i];
            _deicScratch[i] = new DrawElementsIndirectCommand
            {
                Count         = (uint)count,
                InstanceCount = 1u,
                FirstIndex    = firstIndex,
                BaseVertex    = 0,            // baked into indices on upload
                BaseInstance  = 0,
            };
        }
        _walkDrawsThisFrame++;
        DrawRhi(viewProjection, commandCount);
    }

    internal static void AppendCellIndexRuns(
        int sideCellCount, int cellIndex, List<(int Start, int Count)> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (sideCellCount is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sideCellCount),
                sideCellCount,
                "A landscape LOD grid must be 1, 2, 4, or 8 cells per side.");
        }
        if ((uint)cellIndex >= (uint)(sideCellCount * sideCellCount))
            throw new ArgumentOutOfRangeException(nameof(cellIndex));

        int span = LandblockMesh.CellsPerSide / sideCellCount;
        int coarseX = cellIndex / sideCellCount;
        int coarseY = cellIndex % sideCellCount;
        int firstCx = coarseX * span;
        int firstCy = coarseY * span;
        if (span == LandblockMesh.CellsPerSide)
        {
            runs.Add((0, VertsPerLandblock));
            return;
        }
        int runLength = span * LandblockMesh.VerticesPerCell;
        for (int cy = firstCy; cy < firstCy + span; cy++)
        {
            int start = (cy * LandblockMesh.CellsPerSide + firstCx) * LandblockMesh.VerticesPerCell;
            runs.Add((start, runLength));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _retirementLedger.RetryPendingPublications();
        DisposeRhi();
    }

    // ----------------------------------------------------------------
    // Private helpers
    // ----------------------------------------------------------------

    private void EnsureCapacity(int newCapacity)
    {
        if (newCapacity <= _alloc.Capacity)
            return;
        EnsureRhiCapacity(newCapacity);
    }

    private sealed class SlotData
    {
        public uint    LandblockId;
        public Vector3 WorldOrigin;
        public uint    FirstIndex;
        public int     IndexCount;
        public Vector3 AabbMin;
        public Vector3 AabbMax;
    }
}
