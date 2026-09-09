
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

[Flags]
internal enum EnvCellTransparentRoute : byte
{
    None = 0,
    Immediate = 1 << 0,
    Clip = 1 << 1,
    Alpha = 1 << 2,
    All = Immediate | Clip | Alpha,
}

public sealed partial class EnvCellRenderer :
    IDisposable,
    IEnvCellLandblockPublisher
{
    private readonly object _publicationOwner = new();

    private readonly ObjectMeshManager _meshManager;
    private readonly WbFrustum _frustum;

    private readonly ConcurrentDictionary<uint, EnvCellLandblock> _landblocks = new();

    // Active snapshot (atomic swap under _renderLock).
    // WB EnvCellRenderManager.cs:71: private VisibilitySnapshot _activeSnapshot = new();
    private readonly object _renderLock = new();
    private EnvCellVisibilitySnapshot _activeSnapshot = new();

    private Matrix4x4 _lastViewProjection = Matrix4x4.Identity;
    private bool _initialized;

    private readonly List<List<InstanceData>> _listPool = new();
    private int _poolIndex = 0;

    private readonly ThreadLocal<PrepareScratch> _prepareScratch =
        new(() => new PrepareScratch(), trackAllValues: true);

    private Matrix4x4[] _gpuInstanceTransforms = Array.Empty<Matrix4x4>();

    private uint[] _clipSlotData = Array.Empty<uint>();

    private float[] _instanceAlphaData = Array.Empty<float>();

    private float[] _globalLightData = new float[AcDream.Core.Lighting.GlobalLightPacker.FloatsPerLight * 16];
    private int[] _lightSetData = new int[1024 * AcDream.Core.Lighting.LightManager.MaxLightsPerEnvCell];
    private System.Collections.Generic.IReadOnlyList<AcDream.Core.Lighting.LightSource>? _pointSnapshot;
    private sealed class CachedCellLightSet
    {
        public int FrameGeneration;
        public readonly int[] Indices = new int[AcDream.Core.Lighting.LightManager.MaxLightsPerEnvCell];
    }

    private readonly System.Collections.Generic.Dictionary<uint, CachedCellLightSet> _cellLightSetCache = new();
    private readonly List<uint> _cellLightRemovalScratch = new();
    private int _lightFrameGeneration;

    // Per-GPU-fenced-frame-slot draw bookkeeping.
    private int _dynamicFrameSlot;
    private bool _dynamicFrameStarted;

    internal int DynamicBufferSetCount => 0;

    // Reusable scratch arrays — avoid per-frame allocation.
    // WB BaseObjectRenderManager.cs:58-59: private DrawElementsIndirectCommand[] _commands = Array.Empty<...>()
    private DrawElementsIndirectCommand[] _commands = Array.Empty<DrawElementsIndirectCommand>();
    private ModernBatchData[] _modernBatches = Array.Empty<ModernBatchData>();
    private uint[] _detailCategoryData = Array.Empty<uint>();
    private readonly List<EnvCellLandblock> _prepareLandblocks = new();
    private readonly List<InstanceData> _renderInstances = new();
    private readonly List<(ObjectRenderData renderData, ulong gfxObjId, int count, int offset)> _renderDrawCalls = new();
    private readonly Dictionary<ulong, List<InstanceData>> _filteredGroups = new();
    private readonly HashSet<List<InstanceData>> _filteredOwnedLists = new();
    private const int CullGroupCount = 4;
    private const int AdditiveGroupBase = 4;
    private const int ClipDdsGroupBase = 8;
    private const int ClipPalettedGroupBase = 12;
    private const int BatchGroupCount = 16;
    private readonly List<(ObjectRenderBatch batch, int instanceCount, int instanceOffset)>[] _batchesByCullGroup =
        Enumerable.Range(0, BatchGroupCount)
            .Select(_ => new List<(ObjectRenderBatch, int, int)>())
            .ToArray();
    private readonly List<int> _activeCullGroups = new(8);
    private readonly HashSet<uint> _transparentCellIds = new();
    private readonly List<DrawCallRange> _drawCallRanges = new();
    private readonly List<MdiDrawRange> _mdiDrawRanges = new();

    private readonly record struct DrawCallRange(int First, int Count);
    internal readonly record struct MdiDrawRange(
        int GroupIndex,
        int FirstCommand,
        int CommandCount,
        RetailSetSurfaceMaterialState MaterialState);
    private readonly Dictionary<ulong, List<InstanceData>> _activeSnapshotGlobalGroups = new();
    private readonly List<ulong> _activeSnapshotGlobalGfxObjIds = new();

    public bool NeedsPrepare { get; private set; } = true;

    private Matrix4x4 _preparedViewProjection;
    private Vector3 _preparedCameraPosition;
    private readonly HashSet<uint> _preparedFilter = new();
    private bool _preparedFilterWasNull;
    private (int? X, int? Y, int? Radius) _preparedTrim;
    private long _preparedMeshVersion = -1;
    private bool _hasPreparedSnapshot;

    /// <summary>Bumps once per visibility-snapshot rebuild (tests + diagnostics).</summary>
    internal int SnapshotGeneration { get; private set; }

    private RetryableResourceReleaseLedger? _disposeResources;
    private bool _disposing;
    public bool IsDisposed { get; private set; }

    public LastFrameStats Stats => _lastFrameStats;
    public struct LastFrameStats { public int CellsRendered; public int TrianglesDrawn; }
    private LastFrameStats _lastFrameStats;


    public void BeginFrame(int frameSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
        _dynamicFrameSlot = frameSlot;
        _dynamicFrameStarted = true;
        if (++_lightFrameGeneration == 0)
        {
            _cellLightSetCache.Clear();
            _lightFrameGeneration = 1;
        }
    }


    public void SetPointSnapshot(
        System.Collections.Generic.IReadOnlyList<AcDream.Core.Lighting.LightSource>? snapshot)
        => _pointSnapshot = snapshot;


    public static ulong GetEnvCellGeomId(uint environmentId, ushort cellStructure, List<ushort> surfaces)
        => EnvCellLandblockBuildBuilder.ComputeGeometryId(
            environmentId,
            cellStructure,
            surfaces);


    public void CommitLandblock(EnvCellLandblockBuild build)
    {
        EnvCellLandblockPublication publication = PreparePublication(build);
        while (!AdvancePreparationOne(publication))
        {
        }
        CommitPublication(publication);
    }

    EnvCellLandblockPublication IEnvCellLandblockPublisher.PreparePublication(
        EnvCellLandblockBuild build) =>
        PreparePublication(build);

    bool IEnvCellLandblockPublisher.AdvancePreparationOne(
        EnvCellLandblockPublication publication) =>
        AdvancePreparationOne(publication);

    void IEnvCellLandblockPublisher.CommitPublication(
        EnvCellLandblockPublication publication) =>
        CommitPublication(publication);

    internal EnvCellLandblockPublication PreparePublication(
        EnvCellLandblockBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new EnvCellLandblockPublication(_publicationOwner, build);
    }

    internal bool AdvancePreparationOne(
        EnvCellLandblockPublication publication)
    {
        ValidatePublication(publication);
        if (publication.PreparationCommitted)
            return true;

        if (publication.ShellCursor < publication.Build.Shells.Length)
        {
            AddShell(
                publication.Replacement,
                publication.Build.Shells[publication.ShellCursor]);
            WbBoundingBox bounds =
                publication.Build.Shells[publication.ShellCursor].WorldBounds;
            publication.TotalBounds =
                publication.ShellCursor == 0
                    ? bounds
                    : WbBoundingBox.Union(
                        publication.TotalBounds,
                        bounds);
            publication.ShellCursor++;
            return false;
        }

        publication.Replacement.TotalEnvCellBounds =
            publication.Replacement.EnvCellBounds.Count == 0
                ? new WbBoundingBox(Vector3.Zero, Vector3.Zero)
                : publication.TotalBounds;
        publication.Replacement.InstancesReady = true;
        publication.Replacement.MeshDataReady = true;
        publication.Replacement.GpuReady = true;
        publication.PreparationCommitted = true;
        return true;
    }

    internal void CommitPublication(
        EnvCellLandblockPublication publication)
    {
        ValidatePublication(publication);
        if (!publication.PreparationCommitted)
            throw new InvalidOperationException(
                "EnvCell landblock publication cannot commit before preparation.");
        if (publication.PublicationCommitted)
            return;

        _landblocks[publication.Build.LandblockId] = publication.Replacement;
        NeedsPrepare = true;
        publication.PublicationCommitted = true;
    }

    internal static EnvCellLandblock CreateCommittedSnapshot(EnvCellLandblockBuild build)
    {
        var replacement = new EnvCellLandblock
        {
            GridX = (int)((build.LandblockId >> 24) & 0xFFu),
            GridY = (int)((build.LandblockId >> 16) & 0xFFu),
        };

        foreach (var shell in build.Shells)
            AddShell(replacement, shell);

        var total = new WbBoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var bounds in replacement.EnvCellBounds.Values)
            total = WbBoundingBox.Union(total, bounds);
        replacement.TotalEnvCellBounds = replacement.EnvCellBounds.Count == 0
            ? new WbBoundingBox(Vector3.Zero, Vector3.Zero)
            : total;

        replacement.InstancesReady = true;
        replacement.MeshDataReady = true;
        replacement.GpuReady = true;
        return replacement;
    }

    private static void AddShell(
        EnvCellLandblock replacement,
        EnvCellShellPlacement shell)
    {
        replacement.Instances.Add(new EnvCellSceneryInstance
        {
            ObjectId = shell.GeometryId,
            InstanceId = shell.CellId,
            IsBuilding = true,
            IsEntryCell = false,
            WorldPosition = shell.WorldPosition,
            LocalPosition = Vector3.Zero,
            Rotation = shell.Rotation,
            Scale = Vector3.One,
            Transform = shell.Transform,
            LocalBoundingBox = shell.LocalBounds,
            BoundingBox = shell.WorldBounds,
        });
        replacement.EnvCellBounds[shell.CellId] = shell.WorldBounds;

        if (!replacement.BuildingPartGroups.TryGetValue(
                shell.GeometryId,
                out var instances))
        {
            instances = new List<InstanceData>();
            replacement.BuildingPartGroups[shell.GeometryId] = instances;
        }
        instances.Add(new InstanceData
        {
            Transform = shell.Transform,
            CellId = shell.CellId,
            Flags = 0,
        });
    }

    private void ValidatePublication(
        EnvCellLandblockPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!ReferenceEquals(publication.Owner, _publicationOwner))
        {
            throw new ArgumentException(
                "The EnvCell publication receipt belongs to another renderer.",
                nameof(publication));
        }
    }

    /// <summary>
    /// Removes a landblock from the renderer. Future PrepareRenderBatches will exclude it.
    /// </summary>
    public void RemoveLandblock(uint landblockId)
    {
        _landblocks.TryRemove(landblockId, out _);
        uint cellPrefix = landblockId & 0xFFFF0000u;
        _cellLightRemovalScratch.Clear();
        foreach (uint cellId in _cellLightSetCache.Keys)
        {
            if ((cellId & 0xFFFF0000u) == cellPrefix)
                _cellLightRemovalScratch.Add(cellId);
        }
        foreach (uint cellId in _cellLightRemovalScratch)
            _cellLightSetCache.Remove(cellId);
        NeedsPrepare = true;
    }


    public void PrepareRenderBatches(
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        HashSet<uint>? filter = null,
        int? centerLbX = null,
        int? centerLbY = null,
        int? renderRadius = null)
    {
        _lastViewProjection = viewProjection;

        // WB EnvCellRenderManager.cs:249-250:
        if (!_initialized || cameraPosition.Z > 4000) return;

        long meshVersion = _meshManager is null ? 0L : _meshManager.RenderDataAvailabilityVersion;

        if (filter is { Count: 0 })
        {
            if (_hasPreparedSnapshot && !_preparedFilterWasNull && _preparedFilter.Count == 0)
                return;
            lock (_renderLock)
            {
                _poolIndex = 0;
                _activeSnapshot = new EnvCellVisibilitySnapshot();
                _transparentCellIds.Clear();
                NeedsPrepare = false;
            }
            RecordPreparedInputs(viewProjection, cameraPosition, filter, centerLbX, centerLbY, renderRadius, meshVersion);
            return;
        }

        // Prepare gate: every snapshot input unchanged → keep the active snapshot.
        // (Same-thread discipline makes the version sample exact: publish, release
        // tickets, and this method all run on the render thread.)
        if (_hasPreparedSnapshot
            && !NeedsPrepare
            && meshVersion == _preparedMeshVersion
            && _preparedTrim == (centerLbX, centerLbY, renderRadius)
            && FilterUnchanged(filter)
            && CameraApproximatelyEqual(
                viewProjection, cameraPosition,
                _preparedViewProjection, _preparedCameraPosition))
        {
            return;
        }

        // WB EnvCellRenderManager.cs:251-253:
        lock (_renderLock) { _poolIndex = 0; }


        // WB EnvCellRenderManager.cs:262:
        // Filter loaded landblocks by GpuReady + Instances non-empty.
        List<EnvCellLandblock> landblocks = _prepareLandblocks;
        landblocks.Clear();
        foreach (var lb in _landblocks.Values)
        {
            if (centerLbX.HasValue && centerLbY.HasValue && renderRadius.HasValue)
            {
                if (Math.Abs(lb.GridX - centerLbX.Value) > renderRadius.Value ||
                    Math.Abs(lb.GridY - centerLbY.Value) > renderRadius.Value)
                {
                    continue;
                }
            }

            if (lb.GpuReady && lb.Instances.Count > 0)
                landblocks.Add(lb);
        }
        if (landblocks.Count == 0) return;

        foreach (PrepareScratch scratch in _prepareScratch.Values)
            scratch.Reset();

        // WB EnvCellRenderManager.cs:269:
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        // WB EnvCellRenderManager.cs:270-325:
        Parallel.ForEach(landblocks, parallelOptions, lb =>
        {
            lock (lb.Lock)
            {
                var testResult = _frustum.TestBox(lb.TotalEnvCellBounds);
                if (testResult == FrustumTestResult.Outside) return;

                PrepareScratch scratch = _prepareScratch.Value!;

                // WB EnvCellRenderManager.cs:279-295: fast path — LB fully inside.
                if (testResult == FrustumTestResult.Inside)
                {
                    foreach (var (gfxObjId, instances) in lb.BuildingPartGroups)
                        foreach (var instanceData in instances)
                        {
                            if (filter != null && !filter.Contains(instanceData.CellId)) continue;
                            AddToGroups(scratch, instanceData.CellId, gfxObjId, instanceData);
                        }
                    foreach (var (gfxObjId, instances) in lb.StaticPartGroups)
                        foreach (var instanceData in instances)
                        {
                            if (filter != null && !filter.Contains(instanceData.CellId)) continue;
                            AddToGroups(scratch, instanceData.CellId, gfxObjId, instanceData);
                        }
                    return;
                }

                HashSet<uint> visibleCells = scratch.VisibleCells;
                visibleCells.Clear();
                foreach (var kvp in lb.EnvCellBounds)
                {
                    var cellId = kvp.Key;
                    if (filter != null && !filter.Contains(cellId)) continue;
                    if (_frustum.Intersects(kvp.Value))
                        visibleCells.Add(cellId);
                }

                if (visibleCells.Count > 0)
                {
                    foreach (var (gfxObjId, instances) in lb.BuildingPartGroups)
                        foreach (var instanceData in instances)
                        {
                            if (visibleCells.Contains(instanceData.CellId))
                                AddToGroups(scratch, instanceData.CellId, gfxObjId, instanceData);
                        }
                    foreach (var (gfxObjId, instances) in lb.StaticPartGroups)
                        foreach (var instanceData in instances)
                        {
                            if (visibleCells.Contains(instanceData.CellId))
                                AddToGroups(scratch, instanceData.CellId, gfxObjId, instanceData);
                        }
                }
            }
        });

        // WB EnvCellRenderManager.cs:327-373: merge thread-locals + atomic swap.

        var newBatchedByCell    = new Dictionary<uint, Dictionary<ulong, List<InstanceData>>>();
        foreach (PrepareScratch scratch in _prepareScratch.Values)
        {
            foreach (var cellKvp in scratch.BatchedByCell)
            {
                if (!newBatchedByCell.TryGetValue(cellKvp.Key, out var gfxDict))
                {
                    gfxDict = new Dictionary<ulong, List<InstanceData>>();
                    newBatchedByCell[cellKvp.Key] = gfxDict;
                }
                foreach (var gfxKvp in cellKvp.Value)
                {
                    if (!gfxDict.TryGetValue(gfxKvp.Key, out var list))
                    {
                        list = GetPooledList();
                        gfxDict[gfxKvp.Key] = list;
                    }
                    list.AddRange(gfxKvp.Value);
                }
            }
        }

        lock (_renderLock)
        {
            _activeSnapshot = new EnvCellVisibilitySnapshot
            {
                BatchedByCell        = newBatchedByCell,
                VisibleLandblocks    = landblocks,
                PostPreparePoolIndex = _poolIndex,
            };
            RebuildTransparentCellIndex(newBatchedByCell);

            _poolIndex = 0;
            NeedsPrepare = false;
        }
        RecordPreparedInputs(viewProjection, cameraPosition, filter, centerLbX, centerLbY, renderRadius, meshVersion);
    }

    private void RecordPreparedInputs(
        in Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        HashSet<uint>? filter,
        int? centerLbX,
        int? centerLbY,
        int? renderRadius,
        long meshVersion)
    {
        _preparedViewProjection = viewProjection;
        _preparedCameraPosition = cameraPosition;
        _preparedFilterWasNull = filter is null;
        _preparedFilter.Clear();
        if (filter is not null)
            _preparedFilter.UnionWith(filter);
        _preparedTrim = (centerLbX, centerLbY, renderRadius);
        _preparedMeshVersion = meshVersion;
        _hasPreparedSnapshot = true;
        SnapshotGeneration++;
    }

    private bool FilterUnchanged(HashSet<uint>? filter)
    {
        if (filter is null) return _preparedFilterWasNull;
        if (_preparedFilterWasNull) return false;
        return filter.Count == _preparedFilter.Count && _preparedFilter.SetEquals(filter);
    }

    internal static bool CameraApproximatelyEqual(
        in Matrix4x4 vpA, Vector3 eyeA,
        in Matrix4x4 vpB, Vector3 eyeB)
    {
        const float EyeEpsilonSq = 1e-3f * 1e-3f;
        if (Vector3.DistanceSquared(eyeA, eyeB) > EyeEpsilonSq) return false;

        return Close(vpA.M11, vpB.M11) && Close(vpA.M12, vpB.M12) && Close(vpA.M13, vpB.M13) && Close(vpA.M14, vpB.M14)
            && Close(vpA.M21, vpB.M21) && Close(vpA.M22, vpB.M22) && Close(vpA.M23, vpB.M23) && Close(vpA.M24, vpB.M24)
            && Close(vpA.M31, vpB.M31) && Close(vpA.M32, vpB.M32) && Close(vpA.M33, vpB.M33) && Close(vpA.M34, vpB.M34);

        static bool Close(float x, float y)
        {
            const float Rel = 1e-5f;
            return MathF.Abs(x - y) <= Rel * MathF.Max(1f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
        }
    }

    private void RebuildTransparentCellIndex(
        Dictionary<uint, Dictionary<ulong, List<InstanceData>>> batchedByCell)
    {
        _transparentCellIds.Clear();
        foreach ((uint cellId, Dictionary<ulong, List<InstanceData>> groups) in batchedByCell)
        {
            foreach ((ulong gfxObjId, List<InstanceData> transforms) in groups)
            {
                if (transforms.Count == 0)
                    continue;
                ObjectRenderData? renderData = _meshManager.TryGetRenderData(gfxObjId);
                if (renderData is null)
                    continue;
                for (int batchIndex = 0; batchIndex < renderData.Batches.Count; batchIndex++)
                {
                    if (!renderData.Batches[batchIndex].IsTransparent)
                        continue;
                    _transparentCellIds.Add(cellId);
                    goto NextCell;
                }
            }
        NextCell:;
        }
    }


    private static void AddToGroups(
        PrepareScratch scratch,
        uint cellId,
        ulong gfxObjId,
        InstanceData data)
    {
        Dictionary<uint, Dictionary<ulong, List<InstanceData>>> batchedByCell = scratch.BatchedByCell;
        if (!batchedByCell.TryGetValue(cellId, out var gfxDict))
        {
            gfxDict = scratch.RentGfxDictionary();
            batchedByCell[cellId] = gfxDict;
        }
        if (!gfxDict.TryGetValue(gfxObjId, out var list))
        {
            list = scratch.RentList();
            batchedByCell[cellId][gfxObjId] = list;
        }
        list.Add(data);
    }

    private sealed class PrepareScratch
    {
        public readonly Dictionary<uint, Dictionary<ulong, List<InstanceData>>> BatchedByCell = new();
        public readonly HashSet<uint> VisibleCells = new();

        private readonly List<Dictionary<ulong, List<InstanceData>>> _gfxDictionaryPool = new();
        private readonly List<List<InstanceData>> _listPool = new();
        private int _gfxDictionaryIndex;
        private int _listIndex;

        public void Reset()
        {
            BatchedByCell.Clear();
            VisibleCells.Clear();
            _gfxDictionaryIndex = 0;
            _listIndex = 0;
        }

        public Dictionary<ulong, List<InstanceData>> RentGfxDictionary()
        {
            if (_gfxDictionaryIndex == _gfxDictionaryPool.Count)
                _gfxDictionaryPool.Add(new Dictionary<ulong, List<InstanceData>>());
            Dictionary<ulong, List<InstanceData>> value = _gfxDictionaryPool[_gfxDictionaryIndex++];
            value.Clear();
            return value;
        }

        public List<InstanceData> RentList()
        {
            if (_listIndex == _listPool.Count)
                _listPool.Add(new List<InstanceData>());
            List<InstanceData> value = _listPool[_listIndex++];
            value.Clear();
            return value;
        }
    }


    public void Render(WbRenderPass renderPass)
    {
        // WB EnvCellRenderManager.cs:396:
        RenderCore(renderPass, null, null, EnvCellTransparentRoute.All, detailSurfaceActive: false);
    }

    public void Render(WbRenderPass renderPass, HashSet<uint>? filter)
        => RenderCore(renderPass, filter, null, EnvCellTransparentRoute.All, detailSurfaceActive: false);

    public void RenderTransparentOrdered(IReadOnlyList<uint> orderedCellIds)
    {
        ArgumentNullException.ThrowIfNull(orderedCellIds);
        RenderCore(
            WbRenderPass.Transparent,
            null,
            orderedCellIds,
            EnvCellTransparentRoute.All,
            detailSurfaceActive: TransparentDetailEnabled);
    }

    internal void RenderTransparentOrdered(
        IReadOnlyList<uint> orderedCellIds,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive)
    {
        ArgumentNullException.ThrowIfNull(orderedCellIds);
        RenderCore(
            WbRenderPass.Transparent,
            null,
            orderedCellIds,
            route,
            detailSurfaceActive);
    }

    private void RenderCore(
        WbRenderPass renderPass,
        HashSet<uint>? filter,
        IReadOnlyList<uint>? orderedCellIds,
        EnvCellTransparentRoute transparentRoute,
        bool detailSurfaceActive)
    {
        if (!_initialized) return;

        lock (_renderLock)
        {
            var snapshot = _activeSnapshot;
            _poolIndex = snapshot.PostPreparePoolIndex;

            List<InstanceData> allInstances = _renderInstances;
            List<(ObjectRenderData renderData, ulong gfxObjId, int count, int offset)> drawCalls =
                _renderDrawCalls;
            allInstances.Clear();
            drawCalls.Clear();
            _drawCallRanges.Clear();

            if (orderedCellIds is not null)
            {
                for (int cellIndex = 0; cellIndex < orderedCellIds.Count; cellIndex++)
                {
                    uint cellId = orderedCellIds[cellIndex];
                    if (!snapshot.BatchedByCell.TryGetValue(cellId, out var cellGroups))
                        continue;

                    int firstDrawCall = drawCalls.Count;
                    foreach ((ulong gfxObjId, List<InstanceData> transforms) in cellGroups)
                    {
                        if (transforms.Count == 0)
                            continue;
                        ObjectRenderData? renderData = _meshManager.TryGetRenderData(gfxObjId);
                        if (renderData is null || renderData.IsSetup)
                            continue;
                        drawCalls.Add((renderData, gfxObjId, transforms.Count, allInstances.Count));
                        allInstances.AddRange(transforms);
                    }
                    int drawCallCount = drawCalls.Count - firstDrawCall;
                    if (drawCallCount > 0)
                        _drawCallRanges.Add(new DrawCallRange(firstDrawCall, drawCallCount));
                }
            }
            else if (filter is null)
            {
                RebuildUnfilteredGroups(snapshot);
                // WB EnvCellRenderManager.cs:418-429: optimized path — global groups.
                foreach (var gfxObjId in _activeSnapshotGlobalGfxObjIds)
                {
                    if (_activeSnapshotGlobalGroups.TryGetValue(gfxObjId, out var transforms))
                    {
                        var renderData = _meshManager.TryGetRenderData(gfxObjId);
                        if (renderData != null && !renderData.IsSetup)
                        {
                            drawCalls.Add((renderData, gfxObjId, transforms.Count, allInstances.Count));
                            allInstances.AddRange(transforms);
                        }
                    }
                }
            }
            else
            {
                Dictionary<ulong, List<InstanceData>> filteredGroups = _filteredGroups;
                HashSet<List<InstanceData>> ownedLists = _filteredOwnedLists;
                filteredGroups.Clear();
                ownedLists.Clear();

                foreach (var cellId in filter)
                {
                    if (!snapshot.BatchedByCell.TryGetValue(cellId, out var gfxDict)) continue;
                    foreach (var (gfxObjId, transforms) in gfxDict)
                    {
                        if (transforms.Count == 0) continue;
                        if (!filteredGroups.TryGetValue(gfxObjId, out var list))
                        {
                            list = transforms; // Optimization: just use the first list
                            filteredGroups[gfxObjId] = list;
                        }
                        else
                        {
                            if (list == transforms) continue;

                            if (!ownedLists.Contains(list))
                            {
                                var newList = GetPooledList();
                                newList.AddRange(list);
                                list = newList;
                                filteredGroups[gfxObjId] = list;
                                ownedLists.Add(list);
                            }
                            list.AddRange(transforms);
                        }
                    }
                }

                // WB EnvCellRenderManager.cs:461-468:
                foreach (var (gfxObjId, transforms) in filteredGroups)
                {
                    var renderData = _meshManager.TryGetRenderData(gfxObjId);
                    if (renderData != null && !renderData.IsSetup)
                    {
                        drawCalls.Add((renderData, gfxObjId, transforms.Count, allInstances.Count));
                        allInstances.AddRange(transforms);
                    }
                }
            }

            // WB EnvCellRenderManager.cs:470-483:
            if (allInstances.Count > 0)
            {
                if (_drawCallRanges.Count == 0 && drawCalls.Count > 0)
                    _drawCallRanges.Add(new DrawCallRange(0, drawCalls.Count));
                RenderModernMDIInternal(
                    drawCalls,
                    allInstances,
                    _drawCallRanges,
                    renderPass,
                    transparentRoute,
                    detailSurfaceActive);
            }

            // WB EnvCellRenderManager.cs:486-510: selection/hover highlights — DROPPED (no editor state).

            _lastFrameStats.CellsRendered = orderedCellIds?.Count
                ?? filter?.Count
                ?? snapshot.BatchedByCell.Count;
            _lastFrameStats.TrianglesDrawn = 0;
            foreach (var dc in drawCalls)
                _lastFrameStats.TrianglesDrawn += (dc.renderData.Batches.Count > 0
                    ? dc.renderData.Batches[0].IndexCount / 3
                    : 0) * dc.count;
        }
    }

    public bool CellHasTransparent(uint cellId)
        => _transparentCellIds.Contains(cellId);

    internal EnvCellTransparentRoute GetTransparentRoutes(
        uint cellId,
        bool detailSurfaceActive)
    {
        lock (_renderLock)
        {
            if (!_activeSnapshot.BatchedByCell.TryGetValue(cellId, out var groups))
                return EnvCellTransparentRoute.None;

            EnvCellTransparentRoute routes = EnvCellTransparentRoute.None;
            foreach ((ulong gfxObjId, List<InstanceData> transforms) in groups)
            {
                if (transforms.Count == 0)
                    continue;
                ObjectRenderData? renderData = _meshManager.TryGetRenderData(gfxObjId);
                if (renderData is null || renderData.IsSetup)
                    continue;
                for (int batchIndex = 0; batchIndex < renderData.Batches.Count; batchIndex++)
                {
                    ObjectRenderBatch batch = renderData.Batches[batchIndex];
                    if (batch.IsTransparent)
                        routes |= RouteTransparentBatch(batch, detailSurfaceActive);
                }
            }
            return routes;
        }
    }

    internal static EnvCellTransparentRoute RouteTransparentBatch(
        ObjectRenderBatch batch,
        bool detailSurfaceActive)
    {
        RetailAlphaMeshDecision decision = RetailAlphaMeshRouter.Route(
            currentlyDrawingSky: false,
            delayMask: RetailAlphaMeshRouter.DefaultDelayMask,
            detailSurfaceActive: detailSurfaceActive,
            multiPassAlpha: false,
            subsetMask: batch.RetailSurfaceMask,
            materialHasAlpha: false);
        return decision.Action switch
        {
            RetailAlphaMeshAction.Immediate => EnvCellTransparentRoute.Immediate,
            RetailAlphaMeshAction.Append => decision.List == RetailAlphaList.Clip
                ? EnvCellTransparentRoute.Clip
                : EnvCellTransparentRoute.Alpha,
            RetailAlphaMeshAction.AppendClipAndImmediate =>
                EnvCellTransparentRoute.Clip | EnvCellTransparentRoute.Immediate,
            _ => throw new ArgumentOutOfRangeException(nameof(decision.Action)),
        };
    }

    private static bool MatchesTransparentRoute(
        ObjectRenderBatch batch,
        EnvCellTransparentRoute route,
        bool detailSurfaceActive) =>
        (RouteTransparentBatch(batch, detailSurfaceActive) & route) != 0;


    private int[] GetCellLightSet(uint cellId)
    {
        if (!_cellLightSetCache.TryGetValue(cellId, out CachedCellLightSet? cached))
        {
            cached = new CachedCellLightSet();
            _cellLightSetCache.Add(cellId, cached);
        }
        if (cached.FrameGeneration == _lightFrameGeneration)
            return cached.Indices;

        int[] set = cached.Indices;
        System.Array.Fill(set, -1);

        var snap = _pointSnapshot;
        if (snap is { Count: > 0 })
            AcDream.Core.Lighting.LightManager.SelectForCell(snap, set);
        cached.FrameGeneration = _lightFrameGeneration;
        return set;
    }


    private void RebuildUnfilteredGroups(EnvCellVisibilitySnapshot snapshot)
    {
        foreach (List<InstanceData> instances in _activeSnapshotGlobalGroups.Values)
            instances.Clear();
        _activeSnapshotGlobalGfxObjIds.Clear();

        foreach (Dictionary<ulong, List<InstanceData>> cellGroups in snapshot.BatchedByCell.Values)
        {
            foreach ((ulong gfxObjId, List<InstanceData> transforms) in cellGroups)
            {
                if (!_activeSnapshotGlobalGroups.TryGetValue(gfxObjId, out List<InstanceData>? combined))
                {
                    combined = new List<InstanceData>(transforms.Count);
                    _activeSnapshotGlobalGroups.Add(gfxObjId, combined);
                }
                if (combined.Count == 0)
                    _activeSnapshotGlobalGfxObjIds.Add(gfxObjId);
                combined.AddRange(transforms);
            }
        }
    }

    private void RenderModernMDIInternal(
        List<(ObjectRenderData renderData, ulong gfxObjId, int count, int offset)> drawCalls,
        List<InstanceData> allInstances,
        IReadOnlyList<DrawCallRange> drawCallRanges,
        WbRenderPass renderPass,
        EnvCellTransparentRoute transparentRoute,
        bool detailSurfaceActive)
    {
        // WB BaseObjectRenderManager.cs:710-713:
        if (drawCalls.Count == 0 || allInstances.Count == 0) return;

        int passIdx = (int)renderPass;
        if (passIdx < 0 || passIdx > 2) return;

        if (_meshManager.GlobalBuffer is not { HasStores: true })
            return;

        int totalDraws = 0;
        for (int rangeIndex = 0; rangeIndex < drawCallRanges.Count; rangeIndex++)
        {
            DrawCallRange range = drawCallRanges[rangeIndex];
            int rangeEnd = Math.Min(range.First + range.Count, drawCalls.Count);
            for (int callIndex = range.First; callIndex < rangeEnd; callIndex++)
            {
                var call = drawCalls[callIndex];
                foreach (var batch in call.renderData.Batches)
                {
                    // WB BaseObjectRenderManager.cs:723-731: pass-filter.
                    if (!BatchBelongsToPass(batch, renderPass))
                        continue;

                    if (renderPass == WbRenderPass.Transparent
                        && transparentRoute != EnvCellTransparentRoute.All
                        && !MatchesTransparentRoute(batch, transparentRoute, detailSurfaceActive))
                    {
                        continue;
                    }

                    totalDraws++;
                }
            }
        }

        // WB BaseObjectRenderManager.cs:743:
        if (totalDraws == 0) return;
        int uniqueInstanceCount = allInstances.Count;

        if (!_dynamicFrameStarted)
            throw new InvalidOperationException("BeginFrame must be called before drawing EnvCells.");

        // WB BaseObjectRenderManager.cs:761-762: grow scratch arrays.
        if (_commands.Length < totalDraws)
            Array.Resize(ref _commands, Math.Max(_commands.Length * 2, totalDraws));
        if (_modernBatches.Length < totalDraws)
            Array.Resize(ref _modernBatches, Math.Max(_modernBatches.Length * 2, totalDraws));

        _mdiDrawRanges.Clear();
        int cmdIndex = 0;
        for (int rangeIndex = 0; rangeIndex < drawCallRanges.Count; rangeIndex++)
        {
            _activeCullGroups.Clear();
            for (int groupIndex = 0; groupIndex < _batchesByCullGroup.Length; groupIndex++)
                _batchesByCullGroup[groupIndex].Clear();

            DrawCallRange range = drawCallRanges[rangeIndex];
            int rangeEnd = Math.Min(range.First + range.Count, drawCalls.Count);
            for (int callIndex = range.First; callIndex < rangeEnd; callIndex++)
            {
                var call = drawCalls[callIndex];
                foreach (var batch in call.renderData.Batches)
                {
                    if (!BatchBelongsToPass(batch, renderPass))
                        continue;

                    if (renderPass == WbRenderPass.Transparent
                        && transparentRoute != EnvCellTransparentRoute.All
                        && !MatchesTransparentRoute(batch, transparentRoute, detailSurfaceActive))
                    {
                        continue;
                    }

                    int groupIndex = ResolveBatchGroupIndex(
                        batch,
                        renderPass);
                    List<(ObjectRenderBatch batch, int instanceCount, int instanceOffset)> group =
                        _batchesByCullGroup[groupIndex];
                    if (group.Count == 0)
                        _activeCullGroups.Add(groupIndex);
                    group.Add((batch, call.count, call.offset));
                }
            }

            for (int activeIndex = 0; activeIndex < _activeCullGroups.Count; activeIndex++)
            {
                int groupIndex = _activeCullGroups[activeIndex];
                List<(ObjectRenderBatch batch, int instanceCount, int instanceOffset)> group =
                    _batchesByCullGroup[groupIndex];
                foreach (var item in group)
                {
                    _modernBatches[cmdIndex] = new ModernBatchData
                    {
                        TextureTableIndex = item.batch.TextureSlot.Index,
                        SurfaceOpacity    = item.batch.SurfaceOpacity,
                        TextureIndex      = (uint)item.batch.TextureIndex,
                        Flags            = 1u,
                    };

                    _commands[cmdIndex] = new DrawElementsIndirectCommand
                    {
                        Count         = (uint)item.batch.IndexCount,
                        InstanceCount = (uint)item.instanceCount,
                        FirstIndex    = item.batch.FirstIndex,
                        BaseVertex    = (int)item.batch.BaseVertex,
                        BaseInstance  = (uint)item.instanceOffset,
                    };
                    AppendMdiDrawRange(
                        _mdiDrawRanges,
                        groupIndex,
                        cmdIndex,
                        1,
                        item.batch.MaterialState);
                    cmdIndex++;
                }
            }
        }

        SubmitRhi(allInstances, renderPass, totalDraws, uniqueInstanceCount);
    }

    internal static bool BatchBelongsToPass(
        ObjectRenderBatch batch,
        WbRenderPass renderPass) => renderPass switch
        {
            WbRenderPass.Opaque => !batch.IsAdditive && !batch.IsTransparent,
            WbRenderPass.Transparent => batch.IsAdditive || batch.IsTransparent,
            WbRenderPass.SinglePass => true,
            _ => false,
        };

    private static int ResolveBatchGroupIndex(
        ObjectRenderBatch batch,
        WbRenderPass renderPass)
    {
        int cull = (int)batch.CullMode;
        if ((uint)cull >= CullGroupCount)
            throw new ArgumentOutOfRangeException(nameof(batch), batch.CullMode, "Unknown cell-shell cull mode.");

        // Preserve the old SinglePass grouping exactly: non-additive 0..3,
        // additive 4..7. Its submission path still keeps the opaque pipeline.
        if (renderPass != WbRenderPass.Transparent)
            return cull + (batch.IsAdditive ? AdditiveGroupBase : 0);

        if (batch.IsAdditive)
            return cull + AdditiveGroupBase;

        if ((batch.RetailSurfaceMask & RetailAlphaMeshRouter.MaskClipMap) == 0)
            return cull;

        return cull + (batch.Key.PaletteId != 0
            ? ClipPalettedGroupBase
            : ClipDdsGroupBase);
    }

    internal static void AppendMdiDrawRange(
        List<MdiDrawRange> ranges,
        int groupIndex,
        int firstCommand,
        int commandCount,
        RetailSetSurfaceMaterialState materialState)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (commandCount <= 0)
            return;

        if (ranges.Count > 0)
        {
            MdiDrawRange previous = ranges[^1];
            if (previous.GroupIndex == groupIndex
                && previous.MaterialState == materialState
                && previous.FirstCommand + previous.CommandCount == firstCommand)
            {
                ranges[^1] = previous with
                {
                    CommandCount = checked(previous.CommandCount + commandCount),
                };
                return;
            }
        }

        ranges.Add(new MdiDrawRange(groupIndex, firstCommand, commandCount, materialState));
    }


    private List<InstanceData> GetPooledList()
    {
        lock (_listPool)
        {
            if (_poolIndex < _listPool.Count)
            {
                var list = _listPool[_poolIndex++];
                list.Clear();
                return list;
            }
            var fresh = new List<InstanceData>();
            _listPool.Add(fresh);
            _poolIndex++;
            return fresh;
        }
    }


    // ---------------------------------------------------------------------------
    // IDisposable
    // ---------------------------------------------------------------------------

    public void Dispose()
    {
        if (IsDisposed || _disposing) return;
        _disposing = true;
        try
        {
            if (_disposeResources is null)
            {
                var releases = new List<(string Name, Action Release)>
                {
                    ("prepare-scratch", _prepareScratch.Dispose),
                };
                DisposeRhiResources();

                _disposeResources = new RetryableResourceReleaseLedger(releases);
            }

            ResourceReleaseAttempt attempt = _disposeResources.Advance();
            if (!_disposeResources.IsComplete)
            {
                throw attempt.ToException(
                    "One or more EnvCell renderer resources could not be released.");
            }

            _dynamicFrameStarted = false;
            _disposeResources = null;
            IsDisposed = true;

            if (attempt.HasFailures)
            {
                throw attempt.ToException(
                    "EnvCell renderer resources released with exceptional committed outcomes.");
            }
        }
        finally
        {
            _disposing = false;
        }
    }
}
