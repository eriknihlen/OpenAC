using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

public static class InteriorEntityPartition
{
    internal enum ProjectionClass : byte
    {
        OutdoorStatic,
        CellStatic,
        Dynamic,
    }

    internal interface IObserver
    {
        void BeginFrame();

        void Observe(
            uint landblockId,
            WorldEntity entity,
            ProjectionClass projectionClass);

        void Complete(Result result);

        void AbortFrame();
    }

    public sealed class Result
    {
        public Dictionary<uint, List<WorldEntity>> ByCell { get; } = new();
        public List<WorldEntity> OutdoorStatic { get; } = new();
        public List<WorldEntity> Dynamics { get; } = new();

        // MP-Alloc: scratch for PruneEmptyCellBuckets — reused across frames
        // so pruning itself doesn't allocate.
        private readonly List<uint> _emptyCellScratch = new();

        internal void ClearForReuse()
        {
            foreach (var list in ByCell.Values)
                list.Clear();
            OutdoorStatic.Clear();
            Dynamics.Clear();
        }

        internal void PruneEmptyCellBuckets()
        {
            _emptyCellScratch.Clear();
            foreach (var (cellId, list) in ByCell)
            {
                if (list.Count == 0)
                    _emptyCellScratch.Add(cellId);
            }
            foreach (var cellId in _emptyCellScratch)
                ByCell.Remove(cellId);
        }
    }

    public static Result Partition(
        HashSet<uint> visibleCells,
        IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                     IReadOnlyList<WorldEntity> Entities,
                     IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> landblockEntries,
        FrustumPlanes? frustum = null,
        uint neverCullLandblockId = 0u)
    {
        var result = new Result();
        Partition(
            result,
            visibleCells,
            landblockEntries,
            frustum,
            neverCullLandblockId);
        return result;
    }

    public static void Partition(
        Result result,
        HashSet<uint> visibleCells,
        IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                     IReadOnlyList<WorldEntity> Entities,
                     IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> landblockEntries,
        FrustumPlanes? frustum = null,
        uint neverCullLandblockId = 0u)
    {
        result.ClearForReuse();
        foreach (var entry in landblockEntries)
        {
            if (!IsLandblockVisible(
                    entry.LandblockId,
                    entry.AabbMin,
                    entry.AabbMax,
                    frustum,
                    neverCullLandblockId))
            {
                continue;
            }

            foreach (var e in entry.Entities)
            {
                if (e.MeshRefs.Count == 0) continue;

                if (e.ServerGuid != 0)
                {
                    result.Dynamics.Add(e);
                }
                else if (e.ParentCellId is uint cell && IsIndoorCellId(cell))
                {
                    if (!visibleCells.Contains(cell))
                        continue;
                    if (!result.ByCell.TryGetValue(cell, out var list))
                        result.ByCell[cell] = list = new List<WorldEntity>();
                    list.Add(e);
                }
                else
                {
                    result.OutdoorStatic.Add(e);
                }
            }
        }

        result.PruneEmptyCellBuckets();
    }

    internal static void Partition(
        Result result,
        HashSet<uint> visibleCells,
        IEnumerable<(uint LandblockId, Vector3 AabbMin, Vector3 AabbMax,
                     IReadOnlyList<WorldEntity> Entities,
                     IReadOnlyDictionary<uint, WorldEntity>? AnimatedById)> landblockEntries,
        IObserver? observer,
        FrustumPlanes? frustum = null,
        uint neverCullLandblockId = 0u)
    {
        if (observer is null)
        {
            Partition(
                result,
                visibleCells,
                landblockEntries,
                frustum,
                neverCullLandblockId);
            return;
        }

        observer.BeginFrame();
        try
        {
            result.ClearForReuse();
            foreach (var entry in landblockEntries)
            {
                if (!IsLandblockVisible(
                        entry.LandblockId,
                        entry.AabbMin,
                        entry.AabbMax,
                        frustum,
                        neverCullLandblockId))
                {
                    continue;
                }

                foreach (var e in entry.Entities)
                {
                    if (e.MeshRefs.Count == 0) continue;

                    if (e.ServerGuid != 0)
                    {
                        result.Dynamics.Add(e);
                        observer.Observe(
                            entry.LandblockId,
                            e,
                            ProjectionClass.Dynamic);
                    }
                    else if (e.ParentCellId is uint cell && IsIndoorCellId(cell))
                    {
                        if (!visibleCells.Contains(cell))
                            continue;
                        if (!result.ByCell.TryGetValue(cell, out var list))
                            result.ByCell[cell] = list = new List<WorldEntity>();
                        list.Add(e);
                        observer.Observe(
                            entry.LandblockId,
                            e,
                            ProjectionClass.CellStatic);
                    }
                    else
                    {
                        result.OutdoorStatic.Add(e);
                        observer.Observe(
                            entry.LandblockId,
                            e,
                            ProjectionClass.OutdoorStatic);
                    }
                }
            }

            result.PruneEmptyCellBuckets();
            observer.Complete(result);
        }
        catch
        {
            observer.AbortFrame();
            throw;
        }
    }

    public static bool IsIndoorCellId(uint cellId)
    {
        uint low = cellId & 0xFFFFu;
        return low >= 0x0100u && low != 0xFFFFu;
    }

    public static bool IsIndoorCellId(uint? cellId) => cellId is uint c && IsIndoorCellId(c);

    private static bool IsLandblockVisible(
        uint landblockId,
        Vector3 aabbMin,
        Vector3 aabbMax,
        FrustumPlanes? frustum,
        uint neverCullLandblockId) =>
        frustum is null
        || landblockId == neverCullLandblockId
        || FrustumCuller.IsAabbVisible(frustum.Value, aabbMin, aabbMax);
}
