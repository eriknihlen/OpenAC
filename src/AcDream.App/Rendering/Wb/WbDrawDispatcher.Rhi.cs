using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Residency;
using AcDream.App.Rendering.Selection;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using AcDream.Core.Rendering;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

public sealed unsafe partial class WbDrawDispatcher
{
    private readonly IGpuDevice? _device;
    private readonly ICurrentGpuFrameSource? _frames;
    private readonly IWorldPassScope? _scope;

    internal sealed record MeshPipelineSet(
        int SampleCount,
        IGpuPipeline Opaque,
        IGpuPipeline OpaqueAlphaToCoverage,
        IGpuPipeline AlphaBlend,
        IGpuPipeline AlphaBlendDepthWrite,
        IGpuPipeline AlphaAdditive,
        IGpuPipeline AlphaAdditiveDepthWrite,
        IGpuPipeline RawAdditive,
        IGpuPipeline RawAdditiveDepthWrite,
        IGpuPipeline AlphaInverse,
        IGpuPipeline AlphaInverseDepthWrite,
        IGpuPipeline InverseAdditive,
        IGpuPipeline InverseAdditiveDepthWrite);

    private MeshPipelineSet? _backbufferPipelines;
    private MeshPipelineSet? _offscreenPipelines;

    private const string OpaqueTimerScope = "wb-entities-opaque";
    private const string TransparentTimerScope = "wb-entities-transparent";

    private readonly TerrainAtlas.RetailDetailTextureBinding _buildingDetail;
    private readonly Func<bool> _buildingDetailEnabled;

    private readonly record struct RhiSection(
        IGpuBuffer? Buffer,
        uint OffsetBytes,
        uint SizeBytes);

    private readonly WorldTransformFrameArena _worldTransformFrames = new();
    private long _ordinaryTransformDemandFrameSerial = -1;
    private uint _ordinaryTransformDemandThisFrame;
    private uint _ordinaryTransformDemandHighWater;

    private RhiSection _alphaInstances;
    private RhiSection _alphaBatches;
    private RhiSection _alphaClipSlots;
    private RhiSection _alphaGlobalLights;
    private RhiSection _alphaLightSets;
    private RhiSection _alphaIndoor;
    private RhiSection _alphaOpacity;
    private RhiSection _alphaSelectionLighting;
    private RhiSection _alphaDetailCategory;
    private RhiSection _alphaCommands;
    private int _preparedAlphaInstanceCount;
    private uint _alphaTransformBaseInstance;

    internal WorldTransformFrameSlice BeginDirectionalShadowTransformFrame(
        IGpuFrame frame,
        ReadOnlySpan<Matrix4x4> transforms)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _worldTransformFrames.Begin(
            frame,
            transforms,
            ResolveDirectionalShadowTransformBindingSize(transforms.Length));
    }

    internal uint ResolveDirectionalShadowTransformBindingSize(
        int requiredPrefixInstances,
        int ordinaryInstanceUpperBound = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredPrefixInstances);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinaryInstanceUpperBound);
        uint maximum = _device?.Capabilities.MaxStorageBufferRangeBytes
            ?? WorldTransformCapacityPolicy.VulkanGuaranteedMaxStorageBufferRangeBytes;
        uint currentFrameDemand = checked(
            (uint)requiredPrefixInstances + (uint)ordinaryInstanceUpperBound);
        uint requiredCombinedInstances = checked(
            (uint)requiredPrefixInstances + _ordinaryTransformDemandHighWater);
        return WorldTransformCapacityPolicy.ResolveBindingSizeBytes(
            Math.Max(currentFrameDemand, requiredCombinedInstances),
            maximum);
    }

    internal WorldTransformFrameSlice BeginDirectionalShadowTransformFrame(
        IGpuFrame frame,
        in WorldTransformFrameSlice retainedShadowPrefix)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _worldTransformFrames.BeginRetained(
            frame,
            in retainedShadowPrefix);
    }

    internal void CancelDirectionalShadowTransformFrame(IGpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _worldTransformFrames.Cancel(frame);
    }

    internal bool HasDirectionalShadowTransformFrame(long frameSerial) =>
        _worldTransformFrames.IsActiveFor(frameSerial);

    internal uint DirectionalShadowTransformFrameUsedInstances =>
        _worldTransformFrames.UsedInstances;

    internal WbDrawDispatcher(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IWorldPassScope scope,
        TextureCache textures,
        WbMeshAdapter meshAdapter,
        EntitySpawnAdapter entitySpawnAdapter,
        EntityClassificationCache classificationCache,
        AcDream.Core.Rendering.TranslucencyFadeManager translucencyFades,
        IRetailSelectionRenderSink? selectionSink = null,
        RetailAlphaQueue? alphaQueue = null,
        long? alphaScratchBudgetBytes = null,
        TerrainAtlas.RetailDetailTextureBinding buildingDetail = default,
        Func<bool>? buildingDetailEnabled = null,
        Func<uint, float>? hierarchicalTranslucency = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _textures = textures ?? throw new ArgumentNullException(nameof(textures));
        _meshAdapter = meshAdapter ?? throw new ArgumentNullException(nameof(meshAdapter));
        _entitySpawnAdapter = entitySpawnAdapter
            ?? throw new ArgumentNullException(nameof(entitySpawnAdapter));
        _cache = classificationCache
            ?? throw new ArgumentNullException(nameof(classificationCache));
        _translucencyFades = translucencyFades
            ?? throw new ArgumentNullException(nameof(translucencyFades));
        _selectionSink = selectionSink;
        _selectionLighting = selectionSink as IRetailSelectionLightingSource;
        _alphaQueue = alphaQueue;
        _hierarchicalTranslucency = hierarchicalTranslucency;
        _alphaSource = new AlphaDrawSource(this);
        _buildingDetail = buildingDetail;
        _buildingDetailEnabled = buildingDetailEnabled ?? DisableDetailTextures;
        long scratchBudget = alphaScratchBudgetBytes
            ?? AlphaScratchBudgetProfile.Create(
                ResidencyBudgetOptions.Default.AlphaScratchBytes)
                .DispatcherBytes;
        _alphaScratchPolicy = new RetainedScratchCapacityPolicy(scratchBudget);

        int samples = scope.SampleCount;
        try
        {
            _backbufferPipelines = CreateMeshPipelineSet(device, samples);
            _offscreenPipelines = samples == 1
                ? _backbufferPipelines
                : CreateMeshPipelineSet(device, 1);
        }
        catch
        {
            DisposeRhiResources();
            throw;
        }
    }

    private static bool DisableDetailTextures() => false;

    private static MeshPipelineSet CreateMeshPipelineSet(
        IGpuDevice device,
        int samples,
        string baseShaderName = "mesh_modern",
        GpuShaderSet? baseShaders = null,
        string namePrefix = "wb-mesh",
        bool usesRenderPackShaderAbi = false)
    {
        string suffix = samples > 1 ? string.Empty : "-1x";
        var created = new List<IGpuPipeline>(12);
        try
        {
            return new MeshPipelineSet(
                samples,
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-opaque{suffix}", GpuBlendMode.None, true, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-opaque-a2c{suffix}", GpuBlendMode.None, true, true, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-alpha{suffix}", GpuBlendMode.StraightAlpha, false, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-alpha-depth-write{suffix}", GpuBlendMode.StraightAlpha, true, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-additive{suffix}", GpuBlendMode.Additive, false, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-additive-depth-write{suffix}", GpuBlendMode.Additive, true, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-raw-additive{suffix}", GpuBlendMode.RawAdditive, false, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-raw-additive-depth-write{suffix}", GpuBlendMode.RawAdditive, true, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-inverse{suffix}", GpuBlendMode.InverseAlpha, false, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-inverse-depth-write{suffix}", GpuBlendMode.InverseAlpha, true, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-inverse-additive{suffix}", GpuBlendMode.InverseAdditive, false, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)),
                Track(CreateMeshPipeline(
                    device, $"{namePrefix}-inverse-additive-depth-write{suffix}", GpuBlendMode.InverseAdditive, true, false, samples,
                    shaders: baseShaders,
                    shaderName: baseShaderName,
                    usesRenderPackShaderAbi: usesRenderPackShaderAbi)));
        }
        catch
        {
            for (int i = created.Count - 1; i >= 0; i--)
                created[i].Dispose();
            throw;
        }

        IGpuPipeline Track(IGpuPipeline pipeline)
        {
            created.Add(pipeline);
            return pipeline;
        }
    }

    private MeshPipelineSet PipelinesFor(IGpuPassEncoder encoder) =>
        encoder.Pass.SampleCount > 1
            ? _backbufferPipelines!
            : _offscreenPipelines!;

    private static IGpuPipeline CreateMeshPipeline(
        IGpuDevice device,
        string name,
        GpuBlendMode blend,
        bool depthWrite,
        bool alphaToCoverage,
        int sampleCount,
        string shaderName = "mesh_modern",
        GpuShaderSet? shaders = null,
        GpuCompareOp depthCompare = AcDream.App.Rendering.WorldDepthContract.WorldCompare,
        bool usesRenderPackShaderAbi = false) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = shaders ?? new GpuShaderSet(shaderName),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = blend,
            Depth = new GpuDepthState(Test: true, Write: depthWrite, depthCompare),
            Cull = GpuCullMode.Back,
            FrontFace = GpuFrontFace.Clockwise,
            AlphaToCoverage = alphaToCoverage,
            ColorWrite = true,
            UsesRenderPackShaderAbi = usesRenderPackShaderAbi,
            SampleCount = sampleCount,
        });

    private void SubmitRhi(
        Matrix4x4 viewProjection,
        int immediateInstances,
        int totalDraws,
        bool diag)
    {
        IWorldPassScope scope = _scope!;
        IGpuPassEncoder encoder = scope.RequireEncoder();
        IGpuFrame frame = RequireRhiFrame();
        GlobalMeshBuffer mesh = _meshAdapter.MeshManager?.GlobalBuffer
            ?? throw new InvalidOperationException("The shared mesh arena is not published.");

        var pushConstants = new GpuPushConstants
        {
            ViewProjection = viewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 0,
            LightDebug = RenderingDiagnostics.LightDebugMode,
            TextureIndexA = 0,
            TextureIndexB = 0,
            ParamA = 0f,
            ParamB = 0f,
        };

        RhiSection instanceTransforms = WriteWorldTransformSection(
            frame,
            _instanceData.AsSpan(0, immediateInstances * 16),
            out uint transformBaseInstance);
        // Pack receiver/detail shaders subtract this shared-arena prefix for
        // every parallel per-instance array while retaining the absolute pose
        // lookup. The acdream default path always receives zero here.
        pushConstants.TextureIndexB = transformBaseInstance;

        MeshPipelineSet pipelines = PipelinesFor(
            encoder,
            frame,
            out DirectionalShadowFrameBinding shadowBinding);
        IGpuPipeline opaquePipeline = AlphaToCoverage
            ? pipelines.OpaqueAlphaToCoverage
            : pipelines.Opaque;
        BindPipelineWithMesh(
            encoder,
            opaquePipeline,
            mesh);
        encoder.SetPushConstants(in pushConstants);
        BindDirectionalShadowReceiver(encoder, in shadowBinding);

        BindSection(
            encoder,
            GpuBindingModel.StorageInstances,
            instanceTransforms);
        BindRingSection<BatchData>(
            encoder, frame, GpuBindingModel.StorageBatches,
            _batchData.AsSpan(0, totalDraws));
        BindRingSection<uint>(
            encoder, frame, GpuBindingModel.StorageClipSlots,
            _clipSlotData.AsSpan(0, immediateInstances));
        BindGlobalLightsRhi(encoder, frame);
        BindRingSection<int>(
            encoder, frame, GpuBindingModel.StorageInstanceLightSets,
            _lightSetData.AsSpan(0, immediateInstances * LightManager.MaxLightsPerObject));
        BindRingSection<uint>(
            encoder, frame, GpuBindingModel.StorageInstanceIndoor,
            _indoorData.AsSpan(0, immediateInstances));
        BindRingSection<float>(
            encoder, frame, GpuBindingModel.StorageInstanceAlpha,
            _alphaData.AsSpan(0, immediateInstances));
        BindRingSection<Vector2>(
            encoder, frame, GpuBindingModel.StorageInstanceSelectionLighting,
            _selectionLightingData.AsSpan(0, immediateInstances));
        BindRingSection<uint>(
            encoder, frame, GpuBindingModel.StorageInstanceDetailCategory,
            _detailCategoryData.AsSpan(0, immediateInstances));

        AcDream.App.Rendering.WorldFrameSectionBinding.BindClipRegions(
            encoder, scope.Sections, frame);
        AcDream.App.Rendering.WorldFrameSectionBinding.BindSceneLighting(
            encoder, scope.Sections, frame);

        GpuRingAllocation commands = WriteIndirectCommands(
            frame,
            _indirectCommands.AsSpan(0, totalDraws),
            transformBaseInstance);
        IGpuBuffer commandBuffer = commands.Buffer;
        uint commandBase = commands.OffsetBytes;
        ReadOnlySpan<uint> usedDetailCategories =
            _detailCategoryData.AsSpan(0, immediateInstances);
        bool detailEnabled = RetailDetailTextureContract.ShouldRender(
                _buildingDetailEnabled(),
                _buildingDetail)
            && _buildingDetail.Tiling != 0f;

        if (_opaqueDrawCount > 0)
        {
            pushConstants.RenderPass = 0;
            pushConstants.DrawIdOffset = 0;
            encoder.SetPushConstants(in pushConstants);
            using (BeginRhiTimer(encoder, diag, OpaqueTimerScope))
            {
                DrawDetailAwareRangeRhi(
                    encoder, mesh, pipelines, opaquePipeline,
                    ref pushConstants, commandBuffer, commandBase,
                    0, _opaqueDrawCount, usedDetailCategories, detailEnabled);
            }
        }

        if (_transparentDrawCount > 0)
        {
            pushConstants.RenderPass = 1;
            pushConstants.DrawIdOffset = _opaqueDrawCount;
            encoder.SetPushConstants(in pushConstants);
            using (BeginRhiTimer(encoder, diag, TransparentTimerScope))
            {
                DrawImmediateTransparentRhi(
                    encoder,
                    mesh,
                    pipelines,
                    ref pushConstants,
                    commandBuffer,
                    commandBase,
                    usedDetailCategories,
                    detailEnabled);
            }
        }

        SampleRhiTimers(diag);
    }

    /// <summary>
    /// Writes the prepared deferred-alpha payload into the frame ring once. The
    /// sections survive as ordinary values so every later
    /// <c>DrawPreparedAlphaBatch</c> binds the same bytes without recopying.
    /// </summary>
    private void PrepareRhiAlphaSections(int count)
    {
        _preparedAlphaInstanceCount = count;
        IGpuFrame frame = RequireRhiFrame();
        _alphaInstances = WriteWorldTransformSection(
            frame,
            _instanceData.AsSpan(0, count * 16),
            out uint transformBaseInstance);
        _alphaTransformBaseInstance = transformBaseInstance;
        _alphaBatches = WriteRingSection<BatchData>(frame, _batchData.AsSpan(0, count));
        _alphaClipSlots = WriteRingSection<uint>(frame, _clipSlotData.AsSpan(0, count));
        int lightCount = GlobalLightPacker.Pack(_pointSnapshot, ref _globalLightData);
        int uploadCount = lightCount > 0 ? lightCount : 1;
        _alphaGlobalLights = WriteRingSection<float>(
            frame,
            _globalLightData.AsSpan(0, uploadCount * GlobalLightPacker.FloatsPerLight));
        _alphaLightSets = WriteRingSection<int>(
            frame,
            _lightSetData.AsSpan(0, count * LightManager.MaxLightsPerObject));
        _alphaIndoor = WriteRingSection<uint>(frame, _indoorData.AsSpan(0, count));
        _alphaOpacity = WriteRingSection<float>(frame, _alphaData.AsSpan(0, count));
        _alphaSelectionLighting = WriteRingSection<Vector2>(
            frame,
            _selectionLightingData.AsSpan(0, count));
        _alphaDetailCategory = WriteRingSection<uint>(
            frame,
            _detailCategoryData.AsSpan(0, count));
        GpuRingAllocation commands = WriteIndirectCommands(
            frame,
            _indirectCommands.AsSpan(0, count),
            transformBaseInstance);
        _alphaCommands = new RhiSection(
            commands.Buffer,
            commands.OffsetBytes,
            checked((uint)(count * DrawCommandStride)));
    }

    private GpuPushConstants BindAlphaDrawState(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        GlobalMeshBuffer mesh,
        Matrix4x4 viewProjection,
        out MeshPipelineSet pipelines)
    {
        var pushConstants = new GpuPushConstants
        {
            ViewProjection = viewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 1,
            LightDebug = RenderingDiagnostics.LightDebugMode,
            TextureIndexA = 0,
            TextureIndexB = _alphaTransformBaseInstance,
            ParamA = 0f,
            ParamB = 0f,
        };

        pipelines = PipelinesFor(
            encoder,
            frame,
            out DirectionalShadowFrameBinding shadowBinding);
        BindPipelineWithMesh(encoder, pipelines.AlphaBlend, mesh);
        encoder.SetPushConstants(in pushConstants);
        BindDirectionalShadowReceiver(encoder, in shadowBinding);
        BindSection(encoder, GpuBindingModel.StorageInstances, _alphaInstances);
        BindSection(encoder, GpuBindingModel.StorageBatches, _alphaBatches);
        BindSection(encoder, GpuBindingModel.StorageClipSlots, _alphaClipSlots);
        BindSection(encoder, GpuBindingModel.StorageGlobalLights, _alphaGlobalLights);
        BindSection(encoder, GpuBindingModel.StorageInstanceLightSets, _alphaLightSets);
        BindSection(encoder, GpuBindingModel.StorageInstanceIndoor, _alphaIndoor);
        BindSection(encoder, GpuBindingModel.StorageInstanceAlpha, _alphaOpacity);
        BindSection(
            encoder,
            GpuBindingModel.StorageInstanceSelectionLighting,
            _alphaSelectionLighting);
        BindSection(
            encoder,
            GpuBindingModel.StorageInstanceDetailCategory,
            _alphaDetailCategory);
        AcDream.App.Rendering.WorldFrameSectionBinding.BindClipRegions(
            encoder, _scope!.Sections, frame);
        AcDream.App.Rendering.WorldFrameSectionBinding.BindSceneLighting(
            encoder, _scope!.Sections, frame);
        return pushConstants;
    }

    private void DrawPreparedAlphaBatchRhi(
        GlobalMeshBuffer mesh,
        int firstPreparedDraw,
        int drawCount)
    {
        if (_alphaCommands.Buffer is null)
            return;

        IGpuPassEncoder encoder = _scope!.RequireEncoder();
        IGpuFrame frame = RequireRhiFrame();
        GpuPushConstants pushConstants = BindAlphaDrawState(
            encoder, frame, mesh, _deferredAlphaViewProjection, out MeshPipelineSet pipelines);

        if (firstPreparedDraw < 0
            || drawCount < 0
            || firstPreparedDraw > _preparedAlphaInstanceCount - drawCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstPreparedDraw),
                "The prepared-alpha draw range exceeds its uploaded instance/category payload.");
        }

        int runStart = firstPreparedDraw;
        int preparedEnd = firstPreparedDraw + drawCount;
        while (runStart < preparedEnd)
        {
            TranslucencyKind blend = _deferredAlphaKinds[runStart];
            int runEnd = runStart + 1;
            while (runEnd < preparedEnd && _deferredAlphaKinds[runEnd] == blend)
                runEnd++;

            BindPipelineWithMesh(encoder, PipelineForBlend(pipelines, blend), mesh);
            encoder.SetPushConstants(in pushConstants);
            DrawIndirectRangeRhi(
                encoder,
                ref pushConstants,
                _alphaCommands.Buffer!,
                _alphaCommands.OffsetBytes,
                runStart,
                runEnd - runStart);
            runStart = runEnd;
        }
    }

    private void DrawImmediateAlphaInstanceRhi(
        GlobalMeshBuffer mesh,
        TranslucencyKind blend,
        RetailSetSurfaceMaterialState materialState,
        Matrix4x4 viewProjection)
    {
        if (_alphaCommands.Buffer is null)
            return;

        IGpuPassEncoder encoder = _scope!.RequireEncoder();
        IGpuFrame frame = RequireRhiFrame();
        GpuPushConstants pushConstants = BindAlphaDrawState(
            encoder, frame, mesh, viewProjection, out MeshPipelineSet pipelines);

        BindPipelineWithMesh(
            encoder,
            PipelineForMaterial(pipelines, materialState, pipelines.Opaque),
            mesh);
        encoder.SetPushConstants(in pushConstants);
        ArmBuildingDetail(ref pushConstants, materialState);
        DrawIndirectRangeRhi(
            encoder,
            ref pushConstants,
            _alphaCommands.Buffer!,
            _alphaCommands.OffsetBytes,
            startCommand: 0,
            commandCount: 1);
        ClearDetailPushConstants(ref pushConstants);
        encoder.SetPushConstants(in pushConstants);
    }

    private void DrawImmediateTransparentRhi(
        IGpuPassEncoder encoder,
        GlobalMeshBuffer mesh,
        MeshPipelineSet pipelines,
        ref GpuPushConstants pushConstants,
        IGpuBuffer commandBuffer,
        uint commandBase,
        ReadOnlySpan<uint> usedDetailCategories,
        bool detailEnabled)
    {
        int command = _opaqueDrawCount;
        int end = command + _transparentDrawCount;
        while (command < end)
        {
            RetailSetSurfaceMaterialState materialState =
                _groupInputScratch[command].MaterialState;
            bool hasDetail = detailEnabled
                && CommandContainsDetailCategory(
                    _indirectCommands[command],
                    usedDetailCategories);
            int runEnd = command + 1;
            while (runEnd < end
                && !hasDetail
                && (!detailEnabled
                    || !CommandContainsDetailCategory(
                        _indirectCommands[runEnd],
                        usedDetailCategories)))
            {
                runEnd++;
            }

            BindPipelineWithMesh(
                encoder,
                hasDetail
                    ? PipelineForMaterial(pipelines, materialState, pipelines.Opaque)
                    : pipelines.AlphaBlend,
                mesh);
            if (hasDetail)
                ArmBuildingDetail(ref pushConstants, materialState);
            else
                ClearDetailPushConstants(ref pushConstants);
            DrawIndirectRangeRhi(
                encoder,
                ref pushConstants,
                commandBuffer,
                commandBase,
                command,
                runEnd - command);
            command = runEnd;
        }
        ClearDetailPushConstants(ref pushConstants);
        encoder.SetPushConstants(in pushConstants);
    }

    private void DrawDetailAwareRangeRhi(
        IGpuPassEncoder encoder,
        GlobalMeshBuffer mesh,
        MeshPipelineSet pipelines,
        IGpuPipeline opaquePipeline,
        ref GpuPushConstants pushConstants,
        IGpuBuffer commandBuffer,
        uint commandBase,
        int firstCommand,
        int commandCount,
        ReadOnlySpan<uint> detailCategories,
        bool detailEnabled)
    {
        int command = firstCommand;
        int end = firstCommand + commandCount;
        while (command < end)
        {
            bool hasDetail = detailEnabled
                && CommandContainsDetailCategory(
                    _indirectCommands[command], detailCategories);
            int runEnd = command + 1;
            while (runEnd < end
                && !hasDetail
                && (!detailEnabled
                    || !CommandContainsDetailCategory(
                        _indirectCommands[runEnd], detailCategories)))
            {
                runEnd++;
            }

            if (hasDetail)
            {
                RetailSetSurfaceMaterialState materialState =
                    _groupInputScratch[command].MaterialState;
                BindPipelineWithMesh(
                    encoder,
                    PipelineForMaterial(pipelines, materialState, opaquePipeline),
                    mesh);
                ArmBuildingDetail(ref pushConstants, materialState);
            }
            else
            {
                BindPipelineWithMesh(encoder, opaquePipeline, mesh);
                ClearDetailPushConstants(ref pushConstants);
            }
            DrawIndirectRangeRhi(
                encoder,
                ref pushConstants,
                commandBuffer,
                commandBase,
                command,
                runEnd - command);
            command = runEnd;
        }
        ClearDetailPushConstants(ref pushConstants);
        encoder.SetPushConstants(in pushConstants);
    }

    private void ArmBuildingDetail(
        ref GpuPushConstants pushConstants,
        RetailSetSurfaceMaterialState materialState)
    {
        pushConstants.TextureIndexA = _buildingDetail.TextureSlot.Index;
        pushConstants.ParamA = _buildingDetail.Tiling;
        pushConstants.ParamB = materialState.AlphaTestReference;
        if (materialState.FogEnabled)
            pushConstants.RenderPass &= ~RetailDetailTextureContract.NoFogRenderPassFlag;
        else
            pushConstants.RenderPass |= RetailDetailTextureContract.NoFogRenderPassFlag;
    }

    private static void ClearDetailPushConstants(
        ref GpuPushConstants pushConstants)
    {
        pushConstants.TextureIndexA = 0;
        pushConstants.ParamA = 0f;
        pushConstants.ParamB = 0f;
        pushConstants.RenderPass &= ~RetailDetailTextureContract.NoFogRenderPassFlag;
    }

    private static IGpuPipeline PipelineForBlend(MeshPipelineSet pipelines, TranslucencyKind blend) =>
        blend switch
        {
            TranslucencyKind.Additive => pipelines.AlphaAdditive,
            TranslucencyKind.InvAlpha => pipelines.AlphaInverse,
            _ => pipelines.AlphaBlend,
        };

    private static IGpuPipeline PipelineForMaterial(
        MeshPipelineSet pipelines,
        RetailSetSurfaceMaterialState material,
        IGpuPipeline opaquePipeline) => material.Blend switch
        {
            RetailSetSurfaceBlend.Opaque => pipelines.Opaque,
            RetailSetSurfaceBlend.StraightAlpha => material.AlphaTestEnabled
                ? pipelines.AlphaBlendDepthWrite
                : pipelines.AlphaBlend,
            RetailSetSurfaceBlend.AlphaAdditive => material.AlphaTestEnabled
                ? pipelines.AlphaAdditiveDepthWrite
                : pipelines.AlphaAdditive,
            RetailSetSurfaceBlend.Additive => material.AlphaTestEnabled
                ? pipelines.RawAdditiveDepthWrite
                : pipelines.RawAdditive,
            RetailSetSurfaceBlend.InverseAlpha => material.AlphaTestEnabled
                ? pipelines.AlphaInverseDepthWrite
                : pipelines.AlphaInverse,
            RetailSetSurfaceBlend.InverseAdditive => material.AlphaTestEnabled
                ? pipelines.InverseAdditiveDepthWrite
                : pipelines.InverseAdditive,
            RetailSetSurfaceBlend.Clip => opaquePipeline,
            _ => throw new ArgumentOutOfRangeException(nameof(material), material, "Unknown SetSurface blend."),
        };

    private void DrawIndirectRangeRhi(
        IGpuPassEncoder encoder,
        ref GpuPushConstants pushConstants,
        IGpuBuffer commandBuffer,
        uint commandBaseOffsetBytes,
        int startCommand,
        int commandCount,
        CullMode[]? cullModes = null)
    {
        CullMode[] modes = cullModes ?? _drawCullModes;
        int end = startCommand + commandCount;
        int command = startCommand;
        while (command < end)
        {
            CullMode cullMode = modes[command];
            ApplyCullModeRhi(encoder, cullMode);

            int runCount = 1;
            while (command + runCount < end && modes[command + runCount] == cullMode)
                runCount++;

            pushConstants.DrawIdOffset = command;
            encoder.SetPushConstants(in pushConstants);
            encoder.MultiDrawIndexedIndirect(
                commandBuffer,
                commandBaseOffsetBytes + (uint)(command * DrawCommandStride),
                (uint)runCount,
                (uint)DrawCommandStride);

            command += runCount;
        }
    }

    private static void ApplyCullModeRhi(IGpuPassEncoder encoder, CullMode mode)
    {
        encoder.SetFrontFace(GpuFrontFace.Clockwise);
        switch (mode)
        {
            case CullMode.None:
                encoder.SetCullMode(GpuCullMode.None);
                break;
            case CullMode.Clockwise:
                encoder.SetCullMode(GpuCullMode.Front);
                break;
            case CullMode.CounterClockwise:
            case CullMode.Landblock:
                encoder.SetCullMode(GpuCullMode.Back);
                break;
        }
    }

    /// <summary>
    /// Binds a pipeline and immediately re-establishes the mesh source. Every
    /// pipeline owns its own vertex array, and vertex attribute pointers plus the
    /// index binding are vertex-array state, so a pipeline switch inside a pass
    /// silently drops them while storage bindings survive.
    /// </summary>
    private static void BindPipelineWithMesh(
        IGpuPassEncoder encoder,
        IGpuPipeline pipeline,
        GlobalMeshBuffer mesh)
    {
        encoder.BindPipeline(pipeline);
        encoder.BindVertexBuffer(
            0,
            mesh.VertexStore ?? throw new InvalidOperationException(
                "The shared mesh arena has no vertex store."),
            0);
        encoder.BindIndexBuffer(
            mesh.IndexStore ?? throw new InvalidOperationException(
                "The shared mesh arena has no index store."),
            0,
            GpuIndexType.UInt16);
    }

    private void BindGlobalLightsRhi(IGpuPassEncoder encoder, IGpuFrame frame)
    {
        int lightCount = GlobalLightPacker.Pack(_pointSnapshot, ref _globalLightData);
        int uploadCount = lightCount > 0 ? lightCount : 1;
        BindRingSection<float>(
            encoder,
            frame,
            GpuBindingModel.StorageGlobalLights,
            _globalLightData.AsSpan(0, uploadCount * GlobalLightPacker.FloatsPerLight));
    }

    private static void BindRingSection<T>(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        uint binding,
        ReadOnlySpan<T> data)
        where T : unmanaged =>
        BindSection(encoder, binding, WriteRingSection(frame, data));

    private static void BindSection(
        IGpuPassEncoder encoder,
        uint binding,
        in RhiSection section)
    {
        if (section.Buffer is null)
            return;
        encoder.BindStorageBuffer(
            binding,
            section.Buffer,
            section.OffsetBytes,
            section.SizeBytes);
    }

    private static RhiSection WriteRingSection<T>(
        IGpuFrame frame,
        ReadOnlySpan<T> data,
        GpuRingUsage usage = GpuRingUsage.Storage)
        where T : unmanaged
    {
        int elementBytes = sizeof(T);
        int byteCount = Math.Max(data.Length * elementBytes, elementBytes);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, usage);
        if (!data.IsEmpty)
            data.CopyTo(allocation.AsSpan<T>());
        return new RhiSection(allocation.Buffer, allocation.OffsetBytes, (uint)byteCount);
    }

    internal bool NextClassicDrawIsPrivatePass;

    private RhiSection WriteWorldTransformSection(
        IGpuFrame frame,
        ReadOnlySpan<float> matrixFloats,
        out uint firstInstance)
    {
        ResetWorldTransformFrameIfStale(frame.Serial);
        if ((matrixFloats.Length & 15) != 0)
        {
            throw new ArgumentException(
                "World transforms must contain complete 16-float matrices.",
                nameof(matrixFloats));
        }
        ObserveOrdinaryTransformDemand(
            frame.Serial,
            checked((uint)(matrixFloats.Length / 16)));
        bool privatePass = NextClassicDrawIsPrivatePass;
        NextClassicDrawIsPrivatePass = false;
        if (privatePass || !_worldTransformFrames.IsActive)
        {
            firstInstance = 0;
            return WriteRingSection(frame, matrixFloats);
        }

        WorldTransformFrameSlice appended = _worldTransformFrames.Append(
            frame,
            MemoryMarshal.Cast<float, Matrix4x4>(matrixFloats));
        firstInstance = appended.FirstInstance;
        return new RhiSection(
            appended.Buffer,
            appended.BaseOffsetBytes,
            appended.BindingSizeBytes);
    }

    private static GpuRingAllocation WriteIndirectCommands(
        IGpuFrame frame,
        Span<DrawElementsIndirectCommand> commands,
        uint baseInstance)
    {
        int byteCount = checked(commands.Length * DrawCommandStride);
        GpuRingAllocation allocation = frame.AllocateRing(
            byteCount,
            GpuRingUsage.Indirect);
        if (baseInstance == 0)
        {
            MemoryMarshal.AsBytes(commands).CopyTo(allocation.Data);
            return allocation;
        }

        int adjusted = 0;
        try
        {
            for (int i = 0; i < commands.Length; i++)
            {
                commands[i].BaseInstance = checked(
                    commands[i].BaseInstance + baseInstance);
                adjusted++;
            }
            MemoryMarshal.AsBytes(commands).CopyTo(allocation.Data);
        }
        finally
        {
            for (int i = 0; i < adjusted; i++)
                commands[i].BaseInstance -= baseInstance;
        }
        return allocation;
    }

    private void ResetWorldTransformFrameIfStale(long frameSerial)
    {
        _worldTransformFrames.ResetIfStale(frameSerial);
    }

    private void ObserveOrdinaryTransformDemand(long frameSerial, uint instances)
    {
        if (_ordinaryTransformDemandFrameSerial != frameSerial)
        {
            _ordinaryTransformDemandFrameSerial = frameSerial;
            _ordinaryTransformDemandThisFrame = 0;
        }
        _ordinaryTransformDemandThisFrame = checked(
            _ordinaryTransformDemandThisFrame + instances);
        _ordinaryTransformDemandHighWater = Math.Max(
            _ordinaryTransformDemandHighWater,
            _ordinaryTransformDemandThisFrame);
    }

    private void ResetWorldTransformFrame()
    {
        _worldTransformFrames.Reset();
    }

    private IGpuFrame RequireRhiFrame()
    {
        // The same precondition ActivateNextDynamicBufferSet enforces on GL: a
        // draw that has not been bracketed by BeginFrame has no slot to write to.
        if (!_dynamicFrameStarted)
            throw new InvalidOperationException("BeginFrame must be called before drawing world entities.");

        return _frames!.CurrentFrame
            ?? throw new InvalidOperationException(
                "WbDrawDispatcher requires an open IGpuFrame (see GpuDeviceFrameLifetime).");
    }

    private static IDisposable BeginRhiTimer(
        IGpuPassEncoder encoder,
        bool diag,
        string scopeName) =>
        diag ? encoder.BeginTimerScope(scopeName) : NullRhiTimerScope.Instance;

    private void SampleRhiTimers(bool diag)
    {
        if (!diag || _device is null)
            return;

        double totalMs = 0;
        bool any = false;
        if (_device.Timers.TryResolve(OpaqueTimerScope, out double opaqueMs))
        {
            totalMs += opaqueMs;
            any = true;
        }
        if (_device.Timers.TryResolve(TransparentTimerScope, out double transparentMs))
        {
            totalMs += transparentMs;
            any = true;
        }
        if (!any)
            return;

        _gpuSamples[_gpuSampleCursor] = (long)(totalMs * 1000.0);
        _gpuSampleCursor = (_gpuSampleCursor + 1) % _gpuSamples.Length;
    }

    private void DisposeRhiResources()
    {
        MeshPipelineSet? backbuffer = _backbufferPipelines;
        MeshPipelineSet? offscreen = _offscreenPipelines;
        _backbufferPipelines = null;
        _offscreenPipelines = null;
        DisposeMeshPipelineSet(backbuffer);
        if (!ReferenceEquals(offscreen, backbuffer))
            DisposeMeshPipelineSet(offscreen);
        DisposeDirectionalShadowReceiverPipelines();
    }

    private static void DisposeMeshPipelineSet(MeshPipelineSet? pipelines)
    {
        if (pipelines is null)
            return;
        pipelines.Opaque.Dispose();
        pipelines.OpaqueAlphaToCoverage.Dispose();
        pipelines.AlphaBlend.Dispose();
        pipelines.AlphaBlendDepthWrite.Dispose();
        pipelines.AlphaAdditive.Dispose();
        pipelines.AlphaAdditiveDepthWrite.Dispose();
        pipelines.RawAdditive.Dispose();
        pipelines.RawAdditiveDepthWrite.Dispose();
        pipelines.AlphaInverse.Dispose();
        pipelines.AlphaInverseDepthWrite.Dispose();
        pipelines.InverseAdditive.Dispose();
        pipelines.InverseAdditiveDepthWrite.Dispose();
    }

    private sealed class NullRhiTimerScope : IDisposable
    {
        internal static NullRhiTimerScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
