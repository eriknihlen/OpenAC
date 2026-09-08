using System.Numerics;
using AcDream.App.Rendering.Scene;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Walk;

internal sealed class WalkStaticStreamPopulator
{
    private readonly WbDrawDispatcher _dispatcher;
    private readonly List<WbDrawDispatcher.WalkClassifiedBatch> _batchScratch = new();
    private readonly List<WbDrawDispatcher.WalkClassifiedSelectionPart> _selectionScratch = new();
    private readonly List<CellBatch> _cellBatchScratch = new();

    private readonly record struct CellBatch(
        WbDrawDispatcher.WalkClassifiedBatch Batch,
        WalkDrawStage Stage,
        uint LocalEntityId);

    internal WalkStaticStreamPopulator(WbDrawDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    internal void PopulateCell(
        OrderedDrawStream stream,
        WalkDrawStage stage,
        uint cellId,
        ReadOnlySpan<RenderProjectionRecord> records,
        uint tupleLandblockId,
        Vector3 cameraWorldPosition,
        Matrix4x4 viewProjection,
        IWalkLookInViewSource? views = null,
        int viewRouteIndex = -1,
        List<WbDrawDispatcher.WalkClassifiedBatch>? alphaSubmissions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        for (int i = 0; i < records.Length; i++)
        {
            ClassifyAndAppend(
                stream, stage, cellId, in records[i], tupleLandblockId,
                cameraWorldPosition, viewProjection,
                liveDynamic: false, views, viewRouteIndex, alphaSubmissions);
        }
    }

    internal void PopulateBuildingShell(
        OrderedDrawStream stream,
        uint cellId,
        in RenderProjectionRecord record,
        uint tupleLandblockId,
        Vector3 cameraWorldPosition,
        Matrix4x4 viewProjection,
        in WalkBuildingSelection selection,
        Matrix4x4 partZeroTransform,
        List<WbDrawDispatcher.WalkClassifiedBatch> alphaSubmissions)
    {
        ClassifyAndAppend(
            stream,
            WalkDrawStage.BuildingShell,
            cellId,
            in record,
            tupleLandblockId,
            cameraWorldPosition,
            viewProjection,
            alphaSubmissions: alphaSubmissions,
            buildingSelection: selection,
            buildingPartTransform: partZeroTransform);
    }

    internal void PopulateOutdoorStatics(
        OrderedDrawStream stream,
        uint cellId,
        ReadOnlySpan<RenderProjectionRecord> records,
        uint tupleLandblockId,
        Vector3 cameraWorldPosition,
        Matrix4x4 viewProjection,
        IWalkLookInViewSource? views = null,
        int viewRouteIndex = -1,
        ISet<RenderProjectionId>? drawnOnce = null,
        List<WbDrawDispatcher.WalkClassifiedBatch>? alphaSubmissions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        for (int i = 0; i < records.Length; i++)
        {
            if (drawnOnce is not null && !drawnOnce.Add(records[i].Id))
                continue;
            ClassifyAndAppend(
                stream, WalkDrawStage.OutdoorStatic, cellId, in records[i],
                tupleLandblockId, cameraWorldPosition, viewProjection,
                liveDynamic: false, views, viewRouteIndex,
                alphaSubmissions: alphaSubmissions);
        }
    }

    internal void PopulateCellDynamics(
        OrderedDrawStream stream,
        uint cellId,
        ReadOnlySpan<RenderProjectionRecord> records,
        uint tupleLandblockId,
        Vector3 cameraWorldPosition,
        Matrix4x4 viewProjection,
        IWalkLookInViewSource? lookInViews = null,
        int lookInRouteIndex = -1,
        ISet<RenderProjectionId>? drawnOnce = null,
        List<WbDrawDispatcher.WalkClassifiedBatch>? alphaSubmissions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        for (int i = 0; i < records.Length; i++)
        {
            if (drawnOnce is not null && !drawnOnce.Add(records[i].Id))
                continue;
            ClassifyAndAppend(
                stream,
                WalkDrawStage.Dynamic,
                cellId,
                in records[i],
                tupleLandblockId,
                cameraWorldPosition,
                viewProjection,
                liveDynamic: true,
                lookInViews,
                lookInRouteIndex,
                alphaSubmissions: alphaSubmissions);
        }
    }

    internal void PopulateCellObjects(
        OrderedDrawStream stream,
        WalkDrawStage staticStage,
        uint cellId,
        ReadOnlySpan<RenderProjectionRecord> records,
        uint tupleLandblockId,
        Vector3 cameraWorldPosition,
        Matrix4x4 viewProjection,
        IWalkLookInViewSource? views,
        int viewRouteIndex,
        List<WbDrawDispatcher.WalkClassifiedBatch> alphaSubmissions)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(alphaSubmissions);
        _cellBatchScratch.Clear();

        for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
        {
            ref readonly RenderProjectionRecord record = ref records[recordIndex];
            bool liveDynamic = record.ProjectionClass is RenderProjectionClass.LiveDynamicRoot
                or RenderProjectionClass.EquippedChild;
            WalkDrawStage stage = liveDynamic ? WalkDrawStage.Dynamic : staticStage;
            _batchScratch.Clear();
            _selectionScratch.Clear();
            _dispatcher.ClassifyEntityForWalk(
                in record,
                tupleLandblockId,
                _batchScratch,
                _selectionScratch,
                liveDynamic,
                views,
                viewRouteIndex,
                cellId);

            for (int batchIndex = 0; batchIndex < _batchScratch.Count; batchIndex++)
            {
                WbDrawDispatcher.WalkClassifiedBatch batch = _batchScratch[batchIndex];
                Vector3 worldSortCenter = Vector3.Transform(batch.LocalSortCenter, batch.Transform);
                batch = batch with
                {
                    SortDistanceSq = Vector3.DistanceSquared(
                        worldSortCenter, cameraWorldPosition),
                };
                _cellBatchScratch.Add(new CellBatch(
                    batch,
                    stage,
                    record.Source.LocalEntityId));
            }

            for (int selectionIndex = 0; selectionIndex < _selectionScratch.Count; selectionIndex++)
            {
                WbDrawDispatcher.WalkClassifiedSelectionPart part =
                    _selectionScratch[selectionIndex];
                _dispatcher.PublishWalkSelectionPart(in part);
            }
        }

        StableSortCellBatches(_cellBatchScratch);
        for (int i = 0; i < _cellBatchScratch.Count; i++)
        {
            CellBatch item = _cellBatchScratch[i];
            WbDrawDispatcher.WalkClassifiedBatch batch = item.Batch;
            if (batch.IsOpaque)
            {
                stream.Append(new OrderedDrawCommand(
                    batch.Key, batch.Transform, item.Stage, cellId, batch.ClipSlot,
                    batch.Lights, batch.IndoorFlag, batch.Alpha,
                    batch.SelectionLighting, batch.DetailCategory));
            }
            else
            {
                alphaSubmissions.Add(batch);
            }
        }
    }

    private static void StableSortCellBatches(List<CellBatch> batches)
    {
        for (int i = 1; i < batches.Count; i++)
        {
            CellBatch value = batches[i];
            int insertion = i;
            while (insertion > 0
                && value.Batch.SortDistanceSq > batches[insertion - 1].Batch.SortDistanceSq)
            {
                batches[insertion] = batches[insertion - 1];
                insertion--;
            }
            batches[insertion] = value;
        }
    }

    private void ClassifyAndAppend(
        OrderedDrawStream stream,
        WalkDrawStage stage,
        uint cellId,
        in RenderProjectionRecord record,
        uint tupleLandblockId,
        Vector3 cameraWorldPosition,
        Matrix4x4 viewProjection,
        bool liveDynamic = false,
        IWalkLookInViewSource? lookInViews = null,
        int lookInRouteIndex = -1,
        List<WbDrawDispatcher.WalkClassifiedBatch>? alphaSubmissions = null,
        WalkBuildingSelection? buildingSelection = null,
        Matrix4x4 buildingPartTransform = default)
    {
        _batchScratch.Clear();
        _selectionScratch.Clear();
        _dispatcher.ClassifyEntityForWalk(
            in record,
            tupleLandblockId,
            _batchScratch,
            _selectionScratch,
            liveDynamic,
            lookInViews,
            lookInRouteIndex,
            cellId,
            buildingSelection: buildingSelection,
            buildingPartTransform: buildingPartTransform);

        for (int i = 0; i < _batchScratch.Count; i++)
        {
            WbDrawDispatcher.WalkClassifiedBatch batch = _batchScratch[i];
            if (batch.IsOpaque)
            {
                stream.Append(new OrderedDrawCommand(
                    batch.Key, batch.Transform, stage, cellId, batch.ClipSlot,
                    batch.Lights, batch.IndoorFlag, batch.Alpha,
                    batch.SelectionLighting, batch.DetailCategory));
            }
            else
            {
                if (alphaSubmissions is null)
                {
                    _dispatcher.SubmitWalkAlphaInstance(
                        in batch, viewProjection);
                }
                else
                {
                    alphaSubmissions.Add(batch);
                }
            }
        }

        for (int i = 0; i < _selectionScratch.Count; i++)
        {
            WbDrawDispatcher.WalkClassifiedSelectionPart part = _selectionScratch[i];
            _dispatcher.PublishWalkSelectionPart(in part);
        }
    }
}
