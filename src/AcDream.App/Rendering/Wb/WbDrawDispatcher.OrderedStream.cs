using System.Numerics;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Walk;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using AcDream.Core.Rendering;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

public sealed unsafe partial class WbDrawDispatcher
{
    internal readonly record struct OrderedMergeRun(int FirstCommand, int CommandCount);

    private enum PipelineBucket
    {
        Opaque,
        AlphaBlend,
        AlphaAdditive,
        AlphaInverse,
    }

    private static PipelineBucket BucketFor(TranslucencyKind kind)
    {
        if (IsOpaque(kind))
            return PipelineBucket.Opaque;

        return kind switch
        {
            TranslucencyKind.Additive => PipelineBucket.AlphaAdditive,
            TranslucencyKind.InvAlpha => PipelineBucket.AlphaInverse,
            _ => PipelineBucket.AlphaBlend,
        };
    }

    private IGpuPipeline PipelineForBucket(MeshPipelineSet pipelines, PipelineBucket bucket) =>
        bucket switch
        {
            PipelineBucket.Opaque => AlphaToCoverage ? pipelines.OpaqueAlphaToCoverage : pipelines.Opaque,
            PipelineBucket.AlphaAdditive => pipelines.AlphaAdditive,
            PipelineBucket.AlphaInverse => pipelines.AlphaInverse,
            _ => pipelines.AlphaBlend,
        };

    internal static List<OrderedMergeRun> BuildOrderedMergeRuns(
        OrderedDrawStream stream, IReadOnlyList<int>? forcedBreaksAscending = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int count = stream.Count;

        for (int i = 0; i < count; i++)
        {
            if (stream.Stages[i] == WalkDrawStage.PortalPunch)
            {
                throw new NotSupportedException(
                    $"OrderedDrawStream command {i} carries WalkDrawStage.PortalPunch, "
                    + "which has no FW2 submission path — punch geometry emission lands "
                    + "in FW3 with the world wiring "
                    + "(docs/plans/2026-08-30-campaign-fw-frame-walk.md §FW2/§FW3). The "
                    + "stage exists now purely so stage-separation gates can exercise the "
                    + "boundary before the real emission path exists.");
            }
        }

        IReadOnlyList<int> breaks = forcedBreaksAscending ?? Array.Empty<int>();
        int breakCursor = 0;

        var runs = new List<OrderedMergeRun>();
        int cursor = 0;
        while (cursor < count)
        {
            while (breakCursor < breaks.Count && breaks[breakCursor] <= cursor)
                breakCursor++;

            WalkDrawStage stage = stream.Stages[cursor];
            PipelineBucket bucket = BucketFor(stream.Keys[cursor].Translucency);
            CullMode cull = stream.Keys[cursor].CullMode;
            bool detail = stream.DetailCategories[cursor] != 0;

            int end = cursor + 1;
            if (!detail)
            {
                while (end < count
                    && !(breakCursor < breaks.Count && breaks[breakCursor] == end)
                    && stream.Stages[end] == stage
                    && stream.DetailCategories[end] == 0
                    && BucketFor(stream.Keys[end].Translucency) == bucket
                    && stream.Keys[end].CullMode == cull)
                {
                    end++;
                }
            }

            runs.Add(new OrderedMergeRun(cursor, end - cursor));
            cursor = end;
        }

        return runs;
    }

    private static void ValidateMergeRun(OrderedDrawStream stream, OrderedMergeRun run)
    {
        int firstCommand = run.FirstCommand;
        WalkDrawStage stage = stream.Stages[firstCommand];
        PipelineBucket bucket = BucketFor(stream.Keys[firstCommand].Translucency);
        CullMode cull = stream.Keys[firstCommand].CullMode;
        bool detail = stream.DetailCategories[firstCommand] != 0;
        int end = firstCommand + run.CommandCount;

        if (detail && run.CommandCount != 1)
        {
            throw new InvalidOperationException(
                $"Merge run [{firstCommand}, {end}) carries a nonzero DetailCategory but "
                + $"contains {run.CommandCount} commands — a detail-category command must "
                + "emit alone.");
        }

        for (int i = firstCommand + 1; i < end; i++)
        {
            if (stream.Stages[i] != stage)
            {
                throw new InvalidOperationException(
                    $"Merge run [{firstCommand}, {end}) crosses a WalkDrawStage boundary "
                    + $"at command {i} ({stream.Stages[i]} != {stage}) — a merge across a "
                    + "stage boundary is forbidden by construction (Campaign FW §FW2).");
            }
            if (BucketFor(stream.Keys[i].Translucency) != bucket)
            {
                throw new InvalidOperationException(
                    $"Merge run [{firstCommand}, {end}) crosses a pipeline boundary at "
                    + $"command {i} — a merge across a material-state boundary is "
                    + "forbidden by construction (Campaign FW §FW2).");
            }
            if (stream.Keys[i].CullMode != cull)
            {
                throw new InvalidOperationException(
                    $"Merge run [{firstCommand}, {end}) crosses a cull-mode boundary at "
                    + $"command {i} — a merge across a material-state boundary is "
                    + "forbidden by construction (Campaign FW §FW2).");
            }
            if (stream.DetailCategories[i] != 0)
            {
                throw new InvalidOperationException(
                    $"Merge run [{firstCommand}, {end}) contains a detail-category "
                    + $"command at {i} outside a solo run — a detail-category command "
                    + "must emit alone (Campaign FW §FW2).");
            }
        }
    }

    internal (IGpuFrame Frame, IGpuPassEncoder Encoder) RequireWalkSubmission() =>
        (RequireRhiFrame(), _scope!.RequireEncoder());

    internal (int Width, int Height)? WalkAttachmentExtent =>
        _scope is null ? null : (_scope.AttachmentWidth, _scope.AttachmentHeight);


    private OrderedDrawStream? _orderedStream;
    private List<OrderedMergeRun> _orderedRuns = new();
    private int _orderedPreparedCount;
    private IGpuFrame? _orderedFrame;
    private Matrix4x4 _orderedViewProjection;
    private uint _orderedTransformBaseInstance;
    private RhiSection _orderedInstances;
    private RhiSection _orderedBatches;
    private RhiSection _orderedClipSlots;
    private RhiSection _orderedGlobalLights;
    private RhiSection _orderedLightSets;
    private RhiSection _orderedIndoor;
    private RhiSection _orderedAlpha;
    private RhiSection _orderedSelectionLighting;
    private RhiSection _orderedDetailCategory;
    private RhiSection _orderedCommands;

    internal void PrepareOrderedStream(
        IGpuFrame frame,
        OrderedDrawStream stream,
        in Matrix4x4 viewProjection,
        IReadOnlyList<int>? forcedBreaksAscending = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(stream);

        _orderedStream = stream;
        _orderedFrame = frame;
        _orderedPreparedCount = 0;

        _orderedRuns = BuildOrderedMergeRuns(stream, forcedBreaksAscending);

        int count = stream.Count;
        if (count == 0)
            return;

        GlobalMeshBuffer? global = _meshAdapter.MeshManager?.GlobalBuffer;
        if (global is null || !MeshSourceReady())
            return;

        EnsureDeferredAlphaCapacity(count);
        EnsureOrderedCullModeCapacity(count);
        for (int i = 0; i < count; i++)
        {
            GroupKey key = stream.Keys[i];
            WriteMatrix(_instanceData, i * 16, stream.Transforms[i]);
            _clipSlotData[i] = stream.ClipSlots[i];
            _indoorData[i] = stream.IndoorFlags[i];
            _detailCategoryData[i] = stream.DetailCategories[i];
            _alphaData[i] = stream.Alphas[i];
            _selectionLightingData[i] = stream.SelectionLighting[i];
            stream.Lights[i].CopyTo(_lightSetData, i * LightManager.MaxLightsPerObject);

            _batchData[i] = new BatchData
            {
                TextureIndex = key.TextureSlot.Index,
                SurfaceOpacity = key.SurfaceOpacity,
                TextureLayer = key.TextureLayer,
                Flags = 1u | key.FoliageFlags,
            };
            _indirectCommands[i] = new DrawElementsIndirectCommand
            {
                Count = (uint)key.IndexCount,
                InstanceCount = 1,
                FirstIndex = key.FirstIndex,
                BaseVertex = key.BaseVertex,
                BaseInstance = (uint)i,
            };
            _orderedDrawCullModes[i] = key.CullMode;
        }

        _orderedViewProjection = viewProjection;
        _orderedInstances = WriteWorldTransformSection(
            frame, _instanceData.AsSpan(0, count * 16), out uint transformBaseInstance);
        _orderedTransformBaseInstance = transformBaseInstance;
        _orderedBatches = WriteRingSection<BatchData>(frame, _batchData.AsSpan(0, count));
        _orderedClipSlots = WriteRingSection<uint>(frame, _clipSlotData.AsSpan(0, count));
        int lightCount = GlobalLightPacker.Pack(_pointSnapshot, ref _globalLightData);
        int uploadCount = lightCount > 0 ? lightCount : 1;
        _orderedGlobalLights = WriteRingSection<float>(
            frame,
            _globalLightData.AsSpan(0, uploadCount * GlobalLightPacker.FloatsPerLight));
        _orderedLightSets = WriteRingSection<int>(
            frame, _lightSetData.AsSpan(0, count * LightManager.MaxLightsPerObject));
        _orderedIndoor = WriteRingSection<uint>(frame, _indoorData.AsSpan(0, count));
        _orderedAlpha = WriteRingSection<float>(frame, _alphaData.AsSpan(0, count));
        _orderedSelectionLighting = WriteRingSection<Vector2>(
            frame, _selectionLightingData.AsSpan(0, count));
        _orderedDetailCategory = WriteRingSection<uint>(frame, _detailCategoryData.AsSpan(0, count));
        GpuRingAllocation commandsAllocation = WriteIndirectCommands(
            frame, _indirectCommands.AsSpan(0, count), transformBaseInstance);
        _orderedCommands = new RhiSection(
            commandsAllocation.Buffer,
            commandsAllocation.OffsetBytes,
            checked((uint)(count * DrawCommandStride)));

        _orderedPreparedCount = count;
    }

    internal void DrawOrderedRange(IGpuPassEncoder encoder, int firstCommand, int commandCount)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (firstCommand < 0
            || commandCount < 0
            || firstCommand > _orderedPreparedCount - commandCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstCommand),
                "The ordered draw range exceeds the payload the most recent "
                + "PrepareOrderedStream call uploaded.");
        }
        if (commandCount == 0)
            return;
        if (_orderedCommands.Buffer is null || _orderedStream is null)
            return;

        GlobalMeshBuffer? global = _meshAdapter.MeshManager?.GlobalBuffer;
        if (global is null)
            return;

        IGpuFrame frame = _orderedFrame
            ?? throw new InvalidOperationException(
                "DrawOrderedRange has no frame to bind clip-region/"
                + "scene-lighting sections against — PrepareOrderedStream must run first.");
        MeshPipelineSet pipelines = PipelinesFor(
            encoder,
            frame,
            out DirectionalShadowFrameBinding shadowBinding);
        var pushConstants = new GpuPushConstants
        {
            ViewProjection = _orderedViewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 0,
            LightDebug = RenderingDiagnostics.LightDebugMode,
            TextureIndexA = 0,
            TextureIndexB = _orderedTransformBaseInstance,
            ParamA = 0f,
            ParamB = 0f,
        };

        {
            BindPipelineWithMesh(encoder, pipelines.Opaque, global);
            encoder.SetPushConstants(in pushConstants);
            BindDirectionalShadowReceiver(encoder, in shadowBinding);
            BindSection(encoder, GpuBindingModel.StorageInstances, _orderedInstances);
            BindSection(encoder, GpuBindingModel.StorageBatches, _orderedBatches);
            BindSection(encoder, GpuBindingModel.StorageClipSlots, _orderedClipSlots);
            BindSection(encoder, GpuBindingModel.StorageGlobalLights, _orderedGlobalLights);
            BindSection(encoder, GpuBindingModel.StorageInstanceLightSets, _orderedLightSets);
            BindSection(encoder, GpuBindingModel.StorageInstanceIndoor, _orderedIndoor);
            BindSection(encoder, GpuBindingModel.StorageInstanceAlpha, _orderedAlpha);
            BindSection(
                encoder, GpuBindingModel.StorageInstanceSelectionLighting, _orderedSelectionLighting);
            BindSection(
                encoder, GpuBindingModel.StorageInstanceDetailCategory, _orderedDetailCategory);
            AcDream.App.Rendering.WorldFrameSectionBinding.BindClipRegions(
                encoder, _scope!.Sections, frame);
            AcDream.App.Rendering.WorldFrameSectionBinding.BindSceneLighting(
                encoder, _scope!.Sections, frame);
        }

        IGpuBuffer commandBuffer = _orderedCommands.Buffer!;
        uint commandBase = _orderedCommands.OffsetBytes;
        int rangeEnd = firstCommand + commandCount;
        bool detailEnabled = RetailDetailTextureContract.ShouldRender(
                _buildingDetailEnabled(),
                _buildingDetail)
            && _buildingDetail.Tiling != 0f;

        foreach (OrderedMergeRun run in _orderedRuns)
        {
            int runEnd = run.FirstCommand + run.CommandCount;
            if (runEnd <= firstCommand)
                continue;
            if (run.FirstCommand >= rangeEnd)
                break;

            if (run.FirstCommand < firstCommand || runEnd > rangeEnd)
            {
                throw new InvalidOperationException(
                    $"DrawOrderedRange [{firstCommand}, {rangeEnd}) straddles merge run "
                    + $"[{run.FirstCommand}, {runEnd}) — a range boundary must coincide with "
                    + "a run boundary by construction (PrepareOrderedStream's "
                    + "forcedBreaksAscending should have forced a break here; Campaign FW "
                    + "§FW3.4a).");
            }

            ValidateMergeRun(_orderedStream, run);

            PipelineBucket bucket = BucketFor(_orderedStream.Keys[run.FirstCommand].Translucency);
            GroupKey key = _orderedStream.Keys[run.FirstCommand];
            bool hasDetail = detailEnabled
                && _orderedStream.DetailCategories[run.FirstCommand] != 0u;
            IGpuPipeline bucketPipeline = PipelineForBucket(pipelines, bucket);
            IGpuPipeline pipeline = hasDetail
                ? PipelineForMaterial(pipelines, key.MaterialState, bucketPipeline)
                : bucketPipeline;
            pushConstants.RenderPass = bucket == PipelineBucket.Opaque ? 0 : 1;
            if (hasDetail)
                ArmBuildingDetail(ref pushConstants, key.MaterialState);
            else
                ClearDetailPushConstants(ref pushConstants);

            BindPipelineWithMesh(encoder, pipeline, global);
            DrawIndirectRangeRhi(
                encoder, ref pushConstants, commandBuffer, commandBase,
                run.FirstCommand, run.CommandCount, _orderedDrawCullModes);
        }

        ClearDetailPushConstants(ref pushConstants);
        encoder.SetPushConstants(in pushConstants);
    }

    private void EnsureOrderedCullModeCapacity(int count)
    {
        if (_orderedDrawCullModes.Length < count)
            _orderedDrawCullModes = new CullMode[count + 64];
    }
}
