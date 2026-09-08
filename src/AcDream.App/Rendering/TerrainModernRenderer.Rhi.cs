using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.Core.Terrain;

namespace AcDream.App.Rendering;

public sealed unsafe partial class TerrainModernRenderer
{
    internal static readonly GpuVertexLayout TerrainVertexLayout = GpuVertexLayout.Interleaved(
        strideBytes: VertexSize,
        ImmutableArray.Create(
            new GpuVertexAttribute(0, GpuVertexFormat.Float3, 0),
            new GpuVertexAttribute(1, GpuVertexFormat.Float3, 12),
            new GpuVertexAttribute(2, GpuVertexFormat.UByte4UInt, 24),
            new GpuVertexAttribute(3, GpuVertexFormat.UByte4UInt, 28),
            new GpuVertexAttribute(4, GpuVertexFormat.UByte4UInt, 32),
            new GpuVertexAttribute(5, GpuVertexFormat.UByte4UInt, 36)));

    private readonly IGpuDevice? _device;
    private readonly ICurrentGpuFrameSource? _frames;
    private readonly IWorldPassScope? _scope;
    private IGpuPipeline? _pipeline;
    private DirectionalShadowReceiverPipelineState? _directionalShadowReceiver;
    private IGpuBuffer? _vertexStore;
    private IGpuBuffer? _indexStore;
    private IGpuBuffer? _tilingBuffer;

    internal TerrainModernRenderer(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        IWorldPassScope scope,
        TerrainAtlas atlas,
        IGpuResourceRetirementQueue resourceRetirement,
        int initialSlotCapacity = 64)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
        ArgumentNullException.ThrowIfNull(resourceRetirement);
        _retirementLedger = new GpuRetirementLedger(resourceRetirement);
        _alloc = new GpuRetiredTerrainSlotAllocator(initialSlotCapacity, resourceRetirement);
        _slots = new SlotData?[initialSlotCapacity];

        _pipeline = device.CreatePipeline(new GpuPipelineDescription
        {
            Name = "terrain",
            Shaders = new GpuShaderSet("terrain_modern"),
            VertexLayout = TerrainVertexLayout,
            Topology = GpuPrimitiveTopology.TriangleList,
            Blend = GpuBlendMode.None,
            Depth = new GpuDepthState(Test: true, Write: true, WorldDepthContract.WorldCompare),
            Cull = GpuCullMode.Back,
            FrontFace = GpuFrontFace.CounterClockwise,
            AlphaToCoverage = false,
            ColorWrite = true,
            SampleCount = scope.SampleCount,
        });
        AllocateRhiBuffers(initialSlotCapacity);
    }

    private void AllocateRhiBuffers(int capacitySlots)
    {
        long vertexBytes = checked((long)capacitySlots * VertsPerLandblock * VertexSize);
        long indexBytes = checked((long)capacitySlots * IndicesPerLandblock * IndexSize);
        IGpuDevice device = RequireDevice();
        _vertexStore = device.CreateBuffer(new GpuBufferDescription(
            "terrain-vertices",
            vertexBytes,
            GpuBufferUsage.Vertex
                | GpuBufferUsage.TransferSource
                | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        _globalVboCapacityBytes = vertexBytes;
        _indexStore = device.CreateBuffer(new GpuBufferDescription(
            "terrain-indices",
            indexBytes,
            GpuBufferUsage.Index
                | GpuBufferUsage.TransferSource
                | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        _globalEboCapacityBytes = indexBytes;
    }

    private void EnsureRhiCapacity(int newCapacitySlots)
    {
        if (newCapacitySlots <= _alloc.Capacity)
            return;

        long vertexBytes = checked((long)newCapacitySlots * VertsPerLandblock * VertexSize);
        long indexBytes = checked((long)newCapacitySlots * IndicesPerLandblock * IndexSize);
        IGpuDevice device = RequireDevice();
        IGpuBuffer oldVertices = RequireVertexStore();
        IGpuBuffer oldIndices = RequireIndexStore();

        IGpuBuffer newVertices = device.CreateBuffer(new GpuBufferDescription(
            "terrain-vertices",
            vertexBytes,
            GpuBufferUsage.Vertex
                | GpuBufferUsage.TransferSource
                | GpuBufferUsage.TransferDestination,
            GpuMemoryResidency.DeviceLocal));
        IGpuBuffer newIndices;
        try
        {
            newIndices = device.CreateBuffer(new GpuBufferDescription(
                "terrain-indices",
                indexBytes,
                GpuBufferUsage.Index
                    | GpuBufferUsage.TransferSource
                    | GpuBufferUsage.TransferDestination,
                GpuMemoryResidency.DeviceLocal));
        }
        catch
        {
            newVertices.Dispose();
            throw;
        }

        oldVertices.CopyTo(newVertices, 0, 0, _globalVboCapacityBytes);
        oldIndices.CopyTo(newIndices, 0, 0, _globalEboCapacityBytes);

        _vertexStore = newVertices;
        _indexStore = newIndices;
        _globalVboCapacityBytes = vertexBytes;
        _globalEboCapacityBytes = indexBytes;

        // Dispose routes the physical free through the device's retirement queue,
        // so the old arena outlives every frame that can still reference it.
        oldVertices.Dispose();
        oldIndices.Dispose();

        var grownSlots = new SlotData?[newCapacitySlots];
        Array.Copy(_slots, grownSlots, _slots.Length);
        _slots = grownSlots;
        _alloc.GrowTo(newCapacitySlots);
    }

    private void UploadRhiLandblock(
        int slot,
        TerrainVertex[] bakedVerts,
        uint[] bakedIndices)
    {
        RequireVertexStore().Upload(
            (long)slot * VertsPerLandblock * VertexSize,
            MemoryMarshal.AsBytes<TerrainVertex>(bakedVerts));
        RequireIndexStore().Upload(
            (long)slot * IndicesPerLandblock * IndexSize,
            MemoryMarshal.AsBytes<uint>(bakedIndices));
    }

    private void DrawRhi(Matrix4x4 viewProjection, int drawCount)
    {
        IWorldPassScope scope = _scope!;
        IGpuPassEncoder encoder = scope.RequireEncoder();
        IGpuFrame frame = _frames!.CurrentFrame
            ?? throw new InvalidOperationException(
                "TerrainModernRenderer requires an open IGpuFrame (see GpuDeviceFrameLifetime).");

        (GpuTextureSlot terrainSlot, GpuTextureSlot alphaSlot) = _atlas.TextureSlots;

        var pushConstants = new GpuPushConstants
        {
            ViewProjection = viewProjection,
            DrawIdOffset = 0,
            LightingMode = 0,
            RenderPass = 0,
            LightDebug = 0,
            TextureIndexA = terrainSlot.Index,
            TextureIndexB = alphaSlot.Index,
            ParamA = 0f,
            ParamB = 0f,
        };

        IGpuPipeline pipeline = _pipeline!;
        DirectionalShadowFrameBinding shadowBinding = default;
        DirectionalShadowReceiverPipelineState? receiver =
            _directionalShadowReceiver;
        IDirectionalShadowReceiverSource? receiverSource = receiver?.Source;
        bool bindingValid = receiverSource is not null
            && receiverSource.TryGetCurrentFrameBinding(frame, out shadowBinding);
        if (DirectionalShadowReceiverPolicy.ShouldSelectReceiverPipeline(
                encoder.Pass.Name,
                receiverSource is not null,
                bindingValid))
        {
            pipeline = receiver!.Pipeline;
        }

        encoder.BindPipeline(pipeline);
        encoder.SetPushConstants(in pushConstants);
        encoder.BindVertexBuffer(0, RequireVertexStore(), 0);
        encoder.BindIndexBuffer(RequireIndexStore(), 0, GpuIndexType.UInt32);
        BindTilingTable(encoder);
        WorldFrameSectionBinding.BindSceneLighting(encoder, scope.Sections, frame);
        if (shadowBinding.Buffer is not null)
        {
            encoder.BindUniformBuffer(
                GpuBindingModel.UniformDirectionalShadow,
                shadowBinding.Buffer,
                shadowBinding.OffsetBytes,
                shadowBinding.SizeBytes);
        }

        GpuRingAllocation commands = frame.AllocateRing(
            drawCount * sizeof(DrawElementsIndirectCommand),
            GpuRingUsage.Indirect);
        MemoryMarshal.AsBytes(_deicScratch.AsSpan(0, drawCount))
            .CopyTo(commands.Data);
        encoder.MultiDrawIndexedIndirect(
            commands.Buffer,
            commands.OffsetBytes,
            (uint)drawCount,
            (uint)sizeof(DrawElementsIndirectCommand));
    }

    /// <summary>
    /// Binds the immutable 36-entry tiling table. Long-lived and written once, so
    /// its range never moves — which also keeps it out of the descriptor-scope
    /// key's moving parts.
    /// </summary>
    private void BindTilingTable(IGpuPassEncoder encoder)
    {
        if (_tilingBuffer is null)
        {
            if (_atlas.TilingByLayer.Count != TerrainTextureTilingTable.LayerCapacity)
            {
                throw new InvalidOperationException(
                    $"Terrain tiling table has {_atlas.TilingByLayer.Count} entries; " +
                    $"expected {TerrainTextureTilingTable.LayerCapacity}.");
            }

            Span<byte> block = stackalloc byte[TerrainTextureTilingTable.UniformBufferBytes];
            block.Clear();
            for (int i = 0; i < TerrainTextureTilingTable.LayerCapacity; i++)
            {
                BitConverter.TryWriteBytes(
                    block[(i * TerrainTextureTilingTable.UniformElementStrideBytes)..],
                    _atlas.TilingByLayer[i]);
            }

            IGpuBuffer buffer = RequireDevice().CreateBuffer(new GpuBufferDescription(
                "terrain-tiling",
                TerrainTextureTilingTable.UniformBufferBytes,
                GpuBufferUsage.Uniform | GpuBufferUsage.TransferDestination,
                GpuMemoryResidency.DeviceLocal));
            try
            {
                buffer.Upload(0, block);
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
            _tilingBuffer = buffer;
        }

        encoder.BindUniformBuffer(
            GpuBindingModel.UniformTerrainTiling,
            _tilingBuffer,
            0,
            TerrainTextureTilingTable.UniformBufferBytes);
    }

    private IGpuDevice RequireDevice() =>
        _device ?? throw new InvalidOperationException(
            "TerrainModernRenderer's RHI arm was reached without an IGpuDevice.");

    private IGpuBuffer RequireVertexStore() =>
        _vertexStore ?? throw new InvalidOperationException(
            "The terrain vertex arena has not been created.");

    private IGpuBuffer RequireIndexStore() =>
        _indexStore ?? throw new InvalidOperationException(
            "The terrain index arena has not been created.");

    private void DisposeRhi()
    {
        _directionalShadowReceiver?.Dispose();
        _directionalShadowReceiver = null;
        _pipeline?.Dispose();
        _pipeline = null;
        _tilingBuffer?.Dispose();
        _tilingBuffer = null;
        _vertexStore?.Dispose();
        _vertexStore = null;
        _indexStore?.Dispose();
        _indexStore = null;
        _globalVboCapacityBytes = 0;
        _globalEboCapacityBytes = 0;
        _dynamicFrameStarted = false;
        _disposed = true;
    }
}
