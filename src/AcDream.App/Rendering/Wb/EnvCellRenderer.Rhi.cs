using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.Core.Lighting;
using AcDream.Core.Meshing;
using DatReaderWriter.Enums;

namespace AcDream.App.Rendering.Wb;

public sealed unsafe partial class EnvCellRenderer
{
    private readonly IGpuDevice? _device;
    private readonly ICurrentGpuFrameSource? _frames;
    private readonly IWorldPassScope? _scope;
    private IGpuPipeline? _opaquePipeline;
    private IGpuPipeline? _alphaPipeline;
    private IGpuPipeline? _alphaDepthWritePipeline;
    private IGpuPipeline? _clipPipeline;
    private IGpuPipeline? _additivePipeline;
    private IGpuPipeline? _additiveDepthWritePipeline;
    private IGpuPipeline? _rawAdditivePipeline;
    private IGpuPipeline? _rawAdditiveDepthWritePipeline;
    private IGpuPipeline? _inversePipeline;
    private IGpuPipeline? _inverseDepthWritePipeline;
    private IGpuPipeline? _inverseAdditivePipeline;
    private IGpuPipeline? _inverseAdditiveDepthWritePipeline;
    private readonly TerrainAtlas.RetailDetailTextureBinding _environmentDetail;
    private readonly Func<bool> _buildingDetailEnabled;

    internal bool TransparentDetailEnabled =>
        RetailDetailTextureContract.ShouldRender(_buildingDetailEnabled(), _environmentDetail)
        && _environmentDetail.Tiling != 0f;

    internal EnvCellRenderer(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IWorldPassScope scope,
        ObjectMeshManager meshManager,
        WbFrustum frustum,
        TerrainAtlas.RetailDetailTextureBinding environmentDetail = default,
        Func<bool>? buildingDetailEnabled = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
        _frustum = frustum ?? throw new ArgumentNullException(nameof(frustum));
        _environmentDetail = environmentDetail;
        _buildingDetailEnabled = buildingDetailEnabled ?? DisableDetailTextures;

        try
        {
            _opaquePipeline = CreateShellPipeline(
                device, "envcell-opaque", GpuBlendMode.None, depthWrite: true, scope.SampleCount);
            _alphaPipeline = CreateShellPipeline(
                device, "envcell-alpha", GpuBlendMode.StraightAlpha, depthWrite: false, scope.SampleCount);
            _alphaDepthWritePipeline = CreateShellPipeline(
                device, "envcell-alpha-depth-write", GpuBlendMode.StraightAlpha, depthWrite: true, scope.SampleCount);
            _clipPipeline = CreateShellPipeline(
                device,
                "envcell-clip",
                GpuBlendMode.PremultipliedAlpha,
                depthWrite: true,
                scope.SampleCount);
            _additivePipeline = CreateShellPipeline(
                device, "envcell-additive", GpuBlendMode.Additive, depthWrite: false, scope.SampleCount);
            _additiveDepthWritePipeline = CreateShellPipeline(
                device, "envcell-additive-depth-write", GpuBlendMode.Additive, depthWrite: true, scope.SampleCount);
            _rawAdditivePipeline = CreateShellPipeline(
                device, "envcell-raw-additive", GpuBlendMode.RawAdditive, depthWrite: false, scope.SampleCount);
            _rawAdditiveDepthWritePipeline = CreateShellPipeline(
                device, "envcell-raw-additive-depth-write", GpuBlendMode.RawAdditive, depthWrite: true, scope.SampleCount);
            _inversePipeline = CreateShellPipeline(
                device, "envcell-inverse", GpuBlendMode.InverseAlpha, depthWrite: false, scope.SampleCount);
            _inverseDepthWritePipeline = CreateShellPipeline(
                device, "envcell-inverse-depth-write", GpuBlendMode.InverseAlpha, depthWrite: true, scope.SampleCount);
            _inverseAdditivePipeline = CreateShellPipeline(
                device, "envcell-inverse-additive", GpuBlendMode.InverseAdditive, depthWrite: false, scope.SampleCount);
            _inverseAdditiveDepthWritePipeline = CreateShellPipeline(
                device, "envcell-inverse-additive-depth-write", GpuBlendMode.InverseAdditive, depthWrite: true, scope.SampleCount);
            _initialized = true;
        }
        catch
        {
            DisposeRhiResources();
            throw;
        }
    }

    private static bool DisableDetailTextures() => false;

    private static IGpuPipeline CreateShellPipeline(
        IGpuDevice device,
        string name,
        GpuBlendMode blend,
        bool depthWrite,
        int sampleCount,
        string shaderName = "mesh_modern",
        GpuCompareOp depthCompare = AcDream.App.Rendering.WorldDepthContract.WorldCompare) =>
        device.CreatePipeline(new GpuPipelineDescription
        {
            Name = name,
            Shaders = new GpuShaderSet(shaderName),
            VertexLayout = GpuVertexLayout.WorldMesh,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = blend,
            Depth = new GpuDepthState(Test: true, Write: depthWrite, depthCompare),
            Cull = GpuCullMode.Back,
            FrontFace = GpuFrontFace.Clockwise,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = sampleCount,
        });

    /// <summary>
    /// Writes this pass's sections into the frame ring and records the same
    /// per-group multi-draw runs the GL arm issues, in the same order.
    /// </summary>
    private void SubmitRhi(
        List<InstanceData> allInstances,
        WbRenderPass renderPass,
        int totalDraws,
        int uniqueInstanceCount)
    {
        IWorldPassScope scope = _scope!;
        IGpuPassEncoder encoder = scope.RequireEncoder();
        IGpuFrame frame = _frames!.CurrentFrame
            ?? throw new InvalidOperationException(
                "EnvCellRenderer requires an open IGpuFrame (see GpuDeviceFrameLifetime).");
        GlobalMeshBuffer mesh = _meshManager.GlobalBuffer
            ?? throw new InvalidOperationException("The shared mesh arena is not published.");

        if (_gpuInstanceTransforms.Length < uniqueInstanceCount)
        {
            Array.Resize(
                ref _gpuInstanceTransforms,
                Math.Max(_gpuInstanceTransforms.Length * 2, uniqueInstanceCount));
        }
        for (int i = 0; i < uniqueInstanceCount; i++)
            _gpuInstanceTransforms[i] = allInstances[i].Transform;

        if (_clipSlotData.Length < uniqueInstanceCount)
            _clipSlotData = new uint[Math.Max(_clipSlotData.Length * 2, uniqueInstanceCount)];
        Array.Clear(_clipSlotData, 0, uniqueInstanceCount);

        if (_instanceAlphaData.Length < uniqueInstanceCount)
        {
            _instanceAlphaData = new float[
                Math.Max(_instanceAlphaData.Length * 2, uniqueInstanceCount)];
        }
        Array.Fill(_instanceAlphaData, 1f, 0, uniqueInstanceCount);

        int lightStride = LightManager.MaxLightsPerEnvCell;
        if (_lightSetData.Length < uniqueInstanceCount * lightStride)
        {
            _lightSetData = new int[Math.Max(
                _lightSetData.Length * 2,
                uniqueInstanceCount * lightStride)];
        }
        for (int i = 0; i < uniqueInstanceCount; i++)
        {
            int[] cellSet = GetCellLightSet(allInstances[i].CellId);
            Array.Copy(cellSet, 0, _lightSetData, i * lightStride, lightStride);
        }

        int lightCount = GlobalLightPacker.Pack(_pointSnapshot, ref _globalLightData);
        int globalLightUploadCount = lightCount > 0 ? lightCount : 1;

        var pushConstants = new GpuPushConstants
        {
            ViewProjection = _lastViewProjection,
            DrawIdOffset = 0,
            // A7 Fix D D-3/D-4: EnvCell bake — wrap points, no sun.
            LightingMode = 1,
            RenderPass = (int)renderPass,
            LightDebug = AcDream.Core.Rendering.RenderingDiagnostics.LightDebugMode,
            TextureIndexA = 0,
            TextureIndexB = 0,
            ParamA = 0f,
            ParamB = 0f,
        };

        // Bind the pass's base pipeline first so the ring binds land on a live
        // program; the per-range switches below rebind the mesh with it.
        IGpuPipeline basePipeline = renderPass == WbRenderPass.Transparent
            ? _alphaPipeline!
            : _opaquePipeline!;
        BindPipelineWithMesh(encoder, basePipeline, mesh);
        encoder.SetPushConstants(in pushConstants);

        BindRingSection<Matrix4x4>(
            encoder, frame, GpuBindingModel.StorageInstances,
            _gpuInstanceTransforms.AsSpan(0, uniqueInstanceCount));
        BindRingSection<ModernBatchData>(
            encoder, frame, GpuBindingModel.StorageBatches,
            _modernBatches.AsSpan(0, totalDraws));
        BindRingSection<uint>(
            encoder, frame, GpuBindingModel.StorageClipSlots,
            _clipSlotData.AsSpan(0, uniqueInstanceCount));
        BindRingSection<float>(
            encoder, frame, GpuBindingModel.StorageInstanceAlpha,
            _instanceAlphaData.AsSpan(0, uniqueInstanceCount));
        BindRingSection<float>(
            encoder, frame, GpuBindingModel.StorageGlobalLights,
            _globalLightData.AsSpan(
                0,
                globalLightUploadCount * GlobalLightPacker.FloatsPerLight));
        BindRingSection<int>(
            encoder, frame, GpuBindingModel.StorageInstanceLightSets,
            _lightSetData.AsSpan(0, uniqueInstanceCount * lightStride));
        BindEnvironmentDetailCategory(encoder, frame, uniqueInstanceCount);

        // The frame-global sections, bound after this renderer's own binds
        // because those binds are what select the descriptor scope.
        AcDream.App.Rendering.WorldFrameSectionBinding.BindClipRegions(
            encoder, scope.Sections, frame);
        AcDream.App.Rendering.WorldFrameSectionBinding.BindSceneLighting(
            encoder, scope.Sections, frame);

        GpuRingAllocation commands = frame.AllocateRing(
            totalDraws * sizeof(DrawElementsIndirectCommand),
            GpuRingUsage.Indirect);
        MemoryMarshal.AsBytes(_commands.AsSpan(0, totalDraws)).CopyTo(commands.Data);
        IGpuBuffer commandBuffer = commands.Buffer;
        uint commandBase = commands.OffsetBytes;
        bool detailEnabled = TransparentDetailEnabled;

        for (int drawRangeIndex = 0; drawRangeIndex < _mdiDrawRanges.Count; drawRangeIndex++)
        {
            MdiDrawRange drawRange = _mdiDrawRanges[drawRangeIndex];
            int groupIndex = drawRange.GroupIndex;
            CullMode cullMode = ResolveRetailCellShellCullMode(
                (CullMode)(groupIndex % CullGroupCount));

            bool isAdditive = renderPass == WbRenderPass.Transparent
                && groupIndex >= AdditiveGroupBase
                && groupIndex < ClipDdsGroupBase;
            bool isClip = renderPass == WbRenderPass.Transparent
                && groupIndex >= ClipDdsGroupBase;
            IGpuPipeline rangeBasePipeline = isClip
                ? _clipPipeline!
                : isAdditive
                    ? _additivePipeline!
                    : _alphaPipeline!;
            if (detailEnabled)
                rangeBasePipeline = PipelineForMaterial(drawRange.MaterialState);
            if (renderPass == WbRenderPass.Transparent || detailEnabled)
            {
                // Blend state is the pipeline's; switching variants mid-pass has
                // to re-establish the mesh, which is vertex-array state.
                BindPipelineWithMesh(
                    encoder,
                    rangeBasePipeline,
                    mesh);
            }

            SetCullMode(encoder, cullMode);

            pushConstants.RenderPass = isAdditive
                ? (int)renderPass | 0x100
                : (int)renderPass;
            if (detailEnabled && !drawRange.MaterialState.FogEnabled)
                pushConstants.RenderPass |= RetailDetailTextureContract.NoFogRenderPassFlag;
            pushConstants.DrawIdOffset = drawRange.FirstCommand;
            pushConstants.ParamB = detailEnabled
                ? drawRange.MaterialState.AlphaTestReference
                : isClip
                    ? groupIndex >= ClipPalettedGroupBase ? 100f / 255f : 200f / 255f
                    : 0f;
            pushConstants.TextureIndexA = detailEnabled
                ? _environmentDetail.TextureSlot.Index
                : 0u;
            pushConstants.ParamA = detailEnabled
                ? _environmentDetail.Tiling
                : 0f;
            encoder.SetPushConstants(in pushConstants);

            encoder.MultiDrawIndexedIndirect(
                commandBuffer,
                commandBase + (uint)(drawRange.FirstCommand * sizeof(DrawElementsIndirectCommand)),
                (uint)drawRange.CommandCount,
                (uint)sizeof(DrawElementsIndirectCommand));
        }

        pushConstants.TextureIndexA = 0;
        pushConstants.ParamA = 0f;
        pushConstants.ParamB = 0f;
        pushConstants.RenderPass &= ~RetailDetailTextureContract.NoFogRenderPassFlag;
        encoder.SetPushConstants(in pushConstants);
    }

    private IGpuPipeline PipelineForMaterial(RetailSetSurfaceMaterialState material) =>
        material.Blend switch
        {
            RetailSetSurfaceBlend.Opaque => _opaquePipeline!,
            RetailSetSurfaceBlend.StraightAlpha => material.AlphaTestEnabled
                ? _alphaDepthWritePipeline!
                : _alphaPipeline!,
            RetailSetSurfaceBlend.AlphaAdditive => material.AlphaTestEnabled
                ? _additiveDepthWritePipeline!
                : _additivePipeline!,
            RetailSetSurfaceBlend.Additive => material.AlphaTestEnabled
                ? _rawAdditiveDepthWritePipeline!
                : _rawAdditivePipeline!,
            RetailSetSurfaceBlend.InverseAlpha => material.AlphaTestEnabled
                ? _inverseDepthWritePipeline!
                : _inversePipeline!,
            RetailSetSurfaceBlend.InverseAdditive => material.AlphaTestEnabled
                ? _inverseAdditiveDepthWritePipeline!
                : _inverseAdditivePipeline!,
            RetailSetSurfaceBlend.Clip => _clipPipeline!,
            _ => throw new ArgumentOutOfRangeException(nameof(material), material, "Unknown SetSurface blend."),
        };

    private void BindPipelineWithMesh(
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

    private static void SetCullMode(IGpuPassEncoder encoder, CullMode mode)
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

    internal static CullMode ResolveRetailCellShellCullMode(CullMode sidesType)
    {
        _ = sidesType;
        return CullMode.Clockwise;
    }

    private static void BindRingSection<T>(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        uint binding,
        ReadOnlySpan<T> data)
        where T : unmanaged
    {
        int elementBytes = sizeof(T);
        int byteCount = Math.Max(data.Length * elementBytes, elementBytes);
        GpuRingAllocation allocation = frame.AllocateRing(byteCount, GpuRingUsage.Storage);
        if (!data.IsEmpty)
            data.CopyTo(allocation.AsSpan<T>());
        encoder.BindStorageBuffer(
            binding,
            allocation.Buffer,
            allocation.OffsetBytes,
            (uint)byteCount);
    }

    internal void BindEnvironmentDetailCategory(
        IGpuPassEncoder encoder,
        IGpuFrame frame,
        int instanceCount)
    {
        if (_detailCategoryData.Length < instanceCount)
            Array.Resize(ref _detailCategoryData, Math.Max(instanceCount, 16));
        Array.Fill(_detailCategoryData, 1u, 0, instanceCount);
        BindRingSection<uint>(
            encoder,
            frame,
            GpuBindingModel.StorageInstanceDetailCategory,
            _detailCategoryData.AsSpan(0, instanceCount));
    }

    private void DisposeRhiResources()
    {
        _opaquePipeline?.Dispose();
        _opaquePipeline = null;
        _alphaPipeline?.Dispose();
        _alphaPipeline = null;
        _alphaDepthWritePipeline?.Dispose();
        _alphaDepthWritePipeline = null;
        _clipPipeline?.Dispose();
        _clipPipeline = null;
        _additivePipeline?.Dispose();
        _additivePipeline = null;
        _additiveDepthWritePipeline?.Dispose();
        _additiveDepthWritePipeline = null;
        _rawAdditivePipeline?.Dispose();
        _rawAdditivePipeline = null;
        _rawAdditiveDepthWritePipeline?.Dispose();
        _rawAdditiveDepthWritePipeline = null;
        _inversePipeline?.Dispose();
        _inversePipeline = null;
        _inverseDepthWritePipeline?.Dispose();
        _inverseDepthWritePipeline = null;
        _inverseAdditivePipeline?.Dispose();
        _inverseAdditivePipeline = null;
        _inverseAdditiveDepthWritePipeline?.Dispose();
        _inverseAdditiveDepthWritePipeline = null;
    }
}
